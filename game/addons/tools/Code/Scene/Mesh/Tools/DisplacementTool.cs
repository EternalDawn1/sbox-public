namespace Editor.MeshEditor;

using HalfEdgeMesh;

/// <summary>
/// Create and edit displacements on mesh faces.
/// </summary>
[Title( "Displacement" )]
[Icon( "terrain" )]
[Alias( "tools.displacement-tool" )]
[Group( "3" )]
public sealed partial class DisplacementTool( MeshTool tool ) : SelectionTool<MeshFace>( tool )
{
    // Subdivision level (0-5, each level adds one more cut linearly)
    private int _subdivisionLevel = 0;

    // Brush mode enabled for painting
    [Property]
    public bool BrushModeEnabled { get; set; } = false;

    // Which faces to apply displacement to
    public enum ApplyMode
    {
        [Title("Selected Only")]
        SelectedOnly,
        [Title("All Displacements")]
        AllDisplacements
    }

    [Property]
    public ApplyMode Apply { get; set; } = ApplyMode.SelectedOnly;

    // Displacement painting mode
    public enum PaintMode
    {
        [Title("Push/Pull")]
        PushPull,
        Flatten,
        Smooth,
        Inflate,
        Pinch,
        Clay,
        Noise
    }

    [Property]
    public PaintMode Mode { get; set; } = PaintMode.PushPull;

    [Property, Range( 0.1f, 10.0f )]
    public float BrushRadius { get; set; } = 1.0f;

    [Property, Range( 0.01f, 1.0f )]
    public float BrushStrength { get; set; } = 0.1f;

    private MeshFace _hoverFace;
    private SceneDynamicObject _faceObject;
    private bool _isPainting = false;
    private Vector3 _lastPaintPosition;
    private IDisposable _undoScope;
    private Dictionary<MeshComponent, Dictionary<VertexHandle, Vector3>> _originalPositions = new();

    public override void OnEnabled()
    {
        base.OnEnabled();

        CreateFaceObject();
    }

    private void CreateFaceObject()
    {
        var world = Scene?.SceneWorld ?? SceneEditorSession.Active?.Scene?.SceneWorld ?? null;
        if ( world is null )
            return; // can't create face preview without a world

        _faceObject = new SceneDynamicObject( world );
        _faceObject.Material = Material.Load( "materials/tools/vertex_color_translucent.vmat" );
        _faceObject.Attributes.SetCombo( "D_DEPTH_BIAS", 1 );
        _faceObject.Attributes.SetCombo( "D_NO_CULLING", 1 );
        _faceObject.Flags.CastShadows = false;
    }

    public override void OnDisabled()
    {
        base.OnDisabled();

        // End any active paint stroke
        if ( _isPainting )
        {
            EndPaintStroke();
        }

        _faceObject?.Delete();
        _faceObject = null;

        _hoverFace = default;
    }

    public override void OnUpdate()
    {
        base.OnUpdate();

        // Disable move mode gizmo when brush mode is enabled
        if ( BrushModeEnabled )
        {
            Tool.MoveMode = null;
        }
        else if ( Tool.MoveMode == null )
        {
            // Re-enable default move mode when brush mode is disabled
            Tool.SetMoveMode<PositionMode>();
        }

        using var scope = Gizmo.Scope( "DisplacementTool" );

        var result = MeshTrace.Run();
        if ( result.Hit && result.Component is MeshComponent )
            Gizmo.Hitbox.TrySetHovered( result.EndPosition );

        if ( _faceObject is null || !_faceObject.IsValid() )
        {
            // try recreate if possible
            CreateFaceObject();
        }
        else if ( _faceObject.World != Scene?.SceneWorld )
        {
            _hoverFace = default;
            _faceObject.Delete();

            CreateFaceObject();
        }

        if ( Gizmo.IsHovered && Tool.MoveMode?.AllowSceneSelection == true )
        {
            if ( !BrushModeEnabled )
            {
                SelectFace();

                if ( Gizmo.IsDoubleClicked )
                    SelectAllDisplacementFaces();
            }
        }

        // Handle painting only when brush mode is enabled
        if ( BrushModeEnabled && (Apply == ApplyMode.AllDisplacements || Selection.Count > 0) )
        {
            HandlePainting();
        }

        _faceObject.Init( Graphics.PrimitiveType.Triangles );

        if ( _hoverFace.IsValid() )
        {
            var hoverColor = Color.Green.WithAlpha( 0.1f );
            var mesh = _hoverFace.Component.Mesh;
            var vertices = mesh.CreateFace( _hoverFace.Handle, _hoverFace.Transform, hoverColor );
            if ( vertices is not null )
            {
                _faceObject.AddVertex( vertices.AsSpan() );
            }

            _hoverFace = default;
        }

        var selectionColor = Color.Yellow.WithAlpha( 0.1f );
        foreach ( var face in Selection.OfType<MeshFace>() )
        {
            var mesh = face.Component.Mesh;
            var vertices = mesh.CreateFace( face.Handle, face.Transform, selectionColor );
            if ( vertices is not null )
            {
                foreach ( var vertex in vertices )
                    _faceObject.AddVertex( vertex );
            }
        }

        DrawBounds();
    }

    private void HandlePainting()
    {
        var ray = Gizmo.CurrentRay;
        var hit = false;
        var hitPosition = Vector3.Zero;
        var hitFace = default( MeshFace );

        // Get faces to check for ray intersection
        IEnumerable<MeshFace> facesToCheck;
        
        if ( Apply == ApplyMode.AllDisplacements )
        {
            // Optimization: do a scene trace to find hit position and only query nearby meshes
            if ( Scene is null )
            {
                if ( _isPainting ) EndPaintStroke();
                return;
            }

            var sceneTrace = Scene.Trace.Ray( ray, 10000f )
                .UseRenderMeshes( true )
                .Run();

            if ( !sceneTrace.Hit )
            {
                if ( _isPainting )
                {
                    EndPaintStroke();
                }
                return;
            }

            var searchRadius = BrushRadius * 2.0f + 100.0f;
            var nearbyMeshes = Scene.GetAllComponents<MeshComponent>()
                .Where( m => m.IsValid() && m.WorldTransform.Position.Distance( sceneTrace.HitPosition ) < searchRadius )
                .Where( m => m.Mesh is not null && m.Mesh.FaceHandles.Any() && m.Mesh.FaceHandles.Count() > 6 )
                .ToList();

            facesToCheck = nearbyMeshes.SelectMany( component => component.Mesh.FaceHandles.Select( handle => new MeshFace( component, handle ) ) ).ToList();

            if ( !facesToCheck.Any() )
            {
                if ( _isPainting ) EndPaintStroke();
                return;
            }
        }
        else
        {
            // Only use selected faces
            facesToCheck = Selection.OfType<MeshFace>();
            
            // If no faces selected, do nothing
            if ( !facesToCheck.Any() )
            {
                if ( _isPainting )
                {
                    EndPaintStroke();
                }
                return;
            }
        }

        // Find closest face under cursor
        foreach ( var face in facesToCheck )
        {
            var mesh = face.Component.Mesh;
            var transform = face.Transform;

            // Compute face plane
            mesh.ComputeFaceNormal( face.Handle, out var normal );
            var center = mesh.GetFaceCenter( face.Handle );
            var worldCenter = transform.PointToWorld( center );
            var worldNormal = transform.Rotation * normal;
            var plane = new Plane( worldCenter, worldNormal );

            if ( plane.TryTrace( ray, out var pos, true ) )
            {
                // Check if point is inside face bounds (simple AABB check)
                var localPos = transform.PointToLocal( pos );
                var bounds = GetFaceBounds( mesh, face.Handle );
                if ( bounds.Contains( localPos ) )
                {
                    hit = true;
                    hitPosition = pos;
                    hitFace = face;
                    break;
                }
            }
        }

        if ( !hit )
        {
            // If we were painting and now cursor left, end the stroke
            if ( _isPainting )
            {
                EndPaintStroke();
            }
            return;
        }

        // Check if we should start or continue painting
        bool shouldPaint = !_isPainting || !Application.CursorDelta.IsNearZeroLength;

        if ( Gizmo.WasLeftMousePressed )
        {
            // Start new paint stroke
            _isPainting = true;
            _lastPaintPosition = hitPosition;
            _originalPositions.Clear();

            // Create undo scope
            IEnumerable<MeshComponent> affectedComponents;
            
            if ( Apply == ApplyMode.AllDisplacements )
            {
                affectedComponents = GetAllDisplacementFaces()
                    .Select( f => f.Component )
                    .Distinct();
            }
            else
            {
                affectedComponents = Selection.OfType<MeshFace>().Select( x => x.Component ).Distinct();
            }
                
            _undoScope = SceneEditorSession.Active.UndoScope( "Paint Displacement" )
                .WithComponentChanges( affectedComponents.ToArray() )
                .Push();

            shouldPaint = true;
        }

        if ( Gizmo.IsLeftMouseDown && shouldPaint )
        {
            // Apply displacement to faces within brush radius
            ApplyDisplacementToNearbyFaces( hitPosition, facesToCheck );
            _lastPaintPosition = hitPosition;
        }
        else if ( _isPainting && !Gizmo.IsLeftMouseDown )
        {
            // Mouse released - end stroke
            EndPaintStroke();
        }
    }

    private void EndPaintStroke()
    {
        _isPainting = false;

        // Rebuild all affected meshes once at the end
        var affectedComponents = _originalPositions.Keys.ToArray();
        foreach ( var component in affectedComponents )
        {
            // Recompute UVs for all affected faces to fix distortion
            var facesToUpdate = Selection.OfType<MeshFace>()
                .Where( f => f.Component == component )
                .Select( f => f.Handle )
                .ToArray();

            if ( facesToUpdate.Length > 0 )
            {
                component.Mesh.ComputeFaceTextureCoordinatesFromParameters( facesToUpdate );
            }

            // Rebuild mesh once
            component.RebuildMesh();
        }

        _originalPositions.Clear();

        // Dispose undo scope to commit changes
        _undoScope?.Dispose();
        _undoScope = null;
    }

    private void ApplyDisplacement( Vector3 position, IEnumerable<MeshFace> facesToPaint )
    {
        foreach ( var face in facesToPaint )
        {
            var mesh = face.Component.Mesh;
            var transform = face.Transform;
            var component = face.Component;

            // Ensure we have a position dictionary for this component
            if ( !_originalPositions.ContainsKey( component ) )
            {
                _originalPositions[component] = new Dictionary<VertexHandle, Vector3>();
            }

            // Get all vertices of the face
            var vertices = mesh.GetFaceVertices( face.Handle );

            // Get face normal once for this face
            mesh.ComputeFaceNormal( face.Handle, out var faceNormal );
            var worldNormal = transform.Rotation * faceNormal.Normal;

            foreach ( var vertexHandle in vertices )
            {
                var vertexPos = mesh.GetVertexPosition( vertexHandle );
                var worldPos = transform.PointToWorld( vertexPos );

                var distance = worldPos.Distance( position );
                if ( distance > BrushRadius ) continue;

                // Store original position before first modification
                if ( !_originalPositions[component].ContainsKey( vertexHandle ) )
                {
                    _originalPositions[component][vertexHandle] = vertexPos;
                }

                var falloff = 1.0f - (distance / BrushRadius);
                falloff = MathF.Pow( falloff, 2.0f ); // Quadratic falloff

                var strength = BrushStrength * falloff;
                var direction = Gizmo.IsCtrlPressed ? -worldNormal : worldNormal;

                Vector3 newPos = vertexPos;

                switch ( Mode )
                {
                    case PaintMode.PushPull:
                        // Ctrl inverts direction (pull instead of push)
                        newPos = vertexPos + transform.NormalToLocal( direction ) * strength;
                        break;

                    case PaintMode.Flatten:
                        {
                            // Flatten back to original surface - moves vertices toward their original position
                            var originalPos = _originalPositions[component].GetValueOrDefault( vertexHandle, vertexPos );
                            newPos = Vector3.Lerp( vertexPos, originalPos, strength * 2.0f );
                        }
                        break;

                    case PaintMode.Smooth:
                        {
                            // Smooth by averaging with neighbors
                            var smoothed = SmoothVertexPosition( mesh, vertexHandle, strength );
                            newPos = smoothed;
                        }
                        break;

                    case PaintMode.Inflate:
                        {
                            // Push along vertex normal - Ctrl inverts
                            var vertexNormal = GetVertexNormal( mesh, vertexHandle );
                            var inflateDirection = Gizmo.IsCtrlPressed ? -vertexNormal : vertexNormal;
                            newPos = vertexPos + inflateDirection * strength;
                        }
                        break;

                    case PaintMode.Pinch:
                        {
                            // Pull towards brush center - Ctrl inverts (push away)
                            var localBrushPos = transform.PointToLocal( position );
                            var toCenter = localBrushPos - vertexPos;
                            var pinchDirection = Gizmo.IsCtrlPressed ? -toCenter : toCenter;
                            newPos = vertexPos + pinchDirection * strength;
                        }
                        break;

                    case PaintMode.Clay:
                        {
                            // Combination of flatten and push
                            var localBrushPos = transform.PointToLocal( position );
                            var localNormal = transform.NormalToLocal( worldNormal );
                            var plane = new Plane( localBrushPos, localNormal );
                            var projected = plane.SnapToPlane( vertexPos );
                            var flattenPos = Vector3.Lerp( vertexPos, projected, strength );
                            var clayDirection = Gizmo.IsCtrlPressed ? -localNormal : localNormal;
                            newPos = flattenPos + clayDirection * strength * 0.5f;
                        }
                        break;

                    case PaintMode.Noise:
                        {
                            // Add random noise
                            var seed = (int)(worldPos.x * 1000 + worldPos.y * 100 + worldPos.z * 10);
                            var rnd = new Random( seed );
                            var noise = new Vector3(
                                (float)rnd.NextDouble() * 2 - 1,
                                (float)rnd.NextDouble() * 2 - 1,
                                (float)rnd.NextDouble() * 2 - 1
                            );
                            newPos = vertexPos + transform.NormalToLocal( noise ) * strength * 0.5f;
                        }
                        break;
                }

                mesh.SetVertexPosition( vertexHandle, newPos );
            }
        }
    }

    private Vector3 SmoothVertexPosition( PolygonMesh mesh, VertexHandle vertex, float strength )
    {
        mesh.GetEdgesConnectedToVertex( vertex, out var edges );
        if ( edges.Count < 2 ) return mesh.GetVertexPosition( vertex );

        var currentPos = mesh.GetVertexPosition( vertex );
        var averagePos = Vector3.Zero;
        var count = 0;

        foreach ( var edge in edges )
        {
            mesh.GetVerticesConnectedToEdge( edge, out var v1, out var v2 );
            var neighbor = (v1 == vertex) ? v2 : v1;
            averagePos += mesh.GetVertexPosition( neighbor );
            count++;
        }

        if ( count > 0 )
        {
            averagePos /= count;
            return Vector3.Lerp( currentPos, averagePos, strength );
        }

        return currentPos;
    }

    private Vector3 GetVertexNormal( PolygonMesh mesh, VertexHandle vertex )
    {
        mesh.GetFacesConnectedToVertex( vertex, out var faces );
        if ( faces.Count == 0 ) return Vector3.Up;

        var normal = Vector3.Zero;
        foreach ( var face in faces )
        {
            mesh.ComputeFaceNormal( face, out var faceNormal );
            normal += faceNormal.Normal;
        }

        return (normal / faces.Count).Normal;
    }

    private bool IsPointInFace( PolygonMesh mesh, FaceHandle face, Vector3 localPoint )
    {
        // Simple bounding box check
        var bounds = GetFaceBounds( mesh, face );
        return bounds.Contains( localPoint );
    }

    private BBox GetFaceBounds( PolygonMesh mesh, FaceHandle face )
    {
        var vertices = mesh.GetFaceVertices( face );
        var positions = vertices.Select( v => mesh.GetVertexPosition( v ) );
        return BBox.FromPoints( positions );
    }

    private void SelectFace()
    {
        _hoverFace = TraceFace();
        UpdateSelection( _hoverFace );
    }

    private void SelectAllFaces()
    {
        var face = TraceFace();
        if ( !face.IsValid() )
            return;

        if ( !Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Shift ) )
            Selection.Clear();

        if ( !Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Ctrl ) )
        {
            foreach ( var hFace in face.Component.Mesh.FaceHandles )
                Selection.Add( new MeshFace( face.Component, hFace ) );
        }
    }

    private void SelectAllDisplacementFaces()
    {
        if ( !Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Shift ) )
            Selection.Clear();

        foreach ( var face in GetAllDisplacementFaces() )
        {
            Selection.Add( face );
        }
    }

    private IEnumerable<MeshFace> GetAllDisplacementFaces()
    {
        // Get all faces that have subdivision (small faces from QuadSlice)
        // A displacement face is typically smaller than the original face size
        var allMeshes = Scene.GetAllComponents<MeshComponent>();
        
        foreach ( var component in allMeshes )
        {
            var mesh = component.Mesh;
            var faceCount = mesh.FaceHandles.Count();
            
            // If mesh has many faces, it's likely subdivided (displacement)
            // Original meshes usually have fewer, larger faces
            if ( faceCount > 6 ) // More than a simple box
            {
                foreach ( var handle in mesh.FaceHandles )
                {
                    yield return new MeshFace( component, handle );
                }
            }
        }
    }

    private void ApplyDisplacementToNearbyFaces( Vector3 position, IEnumerable<MeshFace> availableFaces )
    {
        // Only process faces within brush radius for performance
        foreach ( var face in availableFaces )
        {
            var mesh = face.Component.Mesh;
            var transform = face.Transform;
            var component = face.Component;

            // Quick distance check - skip faces far from brush
            var faceCenter = mesh.GetFaceCenter( face.Handle );
            var worldCenter = transform.PointToWorld( faceCenter );
            if ( worldCenter.Distance( position ) > BrushRadius * 2.0f )
                continue;

            // Ensure we have a position dictionary for this component
            if ( !_originalPositions.ContainsKey( component ) )
            {
                _originalPositions[component] = new Dictionary<VertexHandle, Vector3>();
            }

            // Get all vertices of the face
            var vertices = mesh.GetFaceVertices( face.Handle );

            // Get face normal once for this face
            mesh.ComputeFaceNormal( face.Handle, out var faceNormal );
            var worldNormal = transform.Rotation * faceNormal.Normal;

            foreach ( var vertexHandle in vertices )
            {
                var vertexPos = mesh.GetVertexPosition( vertexHandle );
                var worldPos = transform.PointToWorld( vertexPos );

                var distance = worldPos.Distance( position );
                if ( distance > BrushRadius ) continue;

                // Store original position before first modification
                if ( !_originalPositions[component].ContainsKey( vertexHandle ) )
                {
                    _originalPositions[component][vertexHandle] = vertexPos;
                }

                var falloff = 1.0f - (distance / BrushRadius);
                falloff = MathF.Pow( falloff, 2.0f ); // Quadratic falloff

                var strength = BrushStrength * falloff;
                var direction = Gizmo.IsCtrlPressed ? -worldNormal : worldNormal;

                Vector3 newPos = vertexPos;

                switch ( Mode )
                {
                    case PaintMode.PushPull:
                        newPos = vertexPos + transform.NormalToLocal( direction ) * strength;
                        break;

                    case PaintMode.Flatten:
                        {
                            var originalPos = _originalPositions[component].GetValueOrDefault( vertexHandle, vertexPos );
                            newPos = Vector3.Lerp( vertexPos, originalPos, strength * 2.0f );
                        }
                        break;

                    case PaintMode.Smooth:
                        {
                            var smoothed = SmoothVertexPosition( mesh, vertexHandle, strength );
                            newPos = smoothed;
                        }
                        break;

                    case PaintMode.Inflate:
                        {
                            var vertexNormal = GetVertexNormal( mesh, vertexHandle );
                            var inflateDirection = Gizmo.IsCtrlPressed ? -vertexNormal : vertexNormal;
                            newPos = vertexPos + inflateDirection * strength;
                        }
                        break;

                    case PaintMode.Pinch:
                        {
                            var localBrushPos = transform.PointToLocal( position );
                            var toCenter = localBrushPos - vertexPos;
                            var pinchDirection = Gizmo.IsCtrlPressed ? -toCenter : toCenter;
                            newPos = vertexPos + pinchDirection * strength;
                        }
                        break;

                    case PaintMode.Clay:
                        {
                            var localBrushPos = transform.PointToLocal( position );
                            var localNormal = transform.NormalToLocal( worldNormal );
                            var plane = new Plane( localBrushPos, localNormal );
                            var projected = plane.SnapToPlane( vertexPos );
                            var flattenPos = Vector3.Lerp( vertexPos, projected, strength );
                            var clayDirection = Gizmo.IsCtrlPressed ? -localNormal : localNormal;
                            newPos = flattenPos + clayDirection * strength * 0.5f;
                        }
                        break;

                    case PaintMode.Noise:
                        {
                            var seed = (int)(worldPos.x * 1000 + worldPos.y * 100 + worldPos.z * 10);
                            var rnd = new Random( seed );
                            var noise = new Vector3(
                                (float)rnd.NextDouble() * 2 - 1,
                                (float)rnd.NextDouble() * 2 - 1,
                                (float)rnd.NextDouble() * 2 - 1
                            );
                            newPos = vertexPos + transform.NormalToLocal( noise ) * strength * 0.5f;
                        }
                        break;
                }

                mesh.SetVertexPosition( vertexHandle, newPos );
            }
        }
    }

    public override Rotation CalculateSelectionBasis()
    {
        if ( Gizmo.Settings.GlobalSpace )
            return Rotation.Identity;

        var face = Selection.OfType<MeshFace>().FirstOrDefault();
        if ( face.IsValid() )
        {
            face.Component.Mesh.ComputeFaceNormal( face.Handle, out var normal );
            var vAxis = ComputeTextureVAxis( normal );
            var basis = Rotation.LookAt( normal, vAxis * -1.0f );
            return face.Transform.RotationToWorld( basis );
        }

        return Rotation.Identity;
    }

    private void DrawBounds()
    {
        using ( Gizmo.Scope( "Face Size" ) )
        {
            Gizmo.Draw.IgnoreDepth = true;
            Gizmo.Draw.Color = Color.White;
            Gizmo.Draw.LineThickness = 4;

            var box = CalculateSelectionBounds();
            var textSize = 22 * Gizmo.Settings.GizmoScale * Application.DpiScale;

            Gizmo.Draw.Color = Gizmo.Colors.Active.WithAlpha( 0.5f );
            Gizmo.Draw.LineThickness = 1;
            Gizmo.Draw.LineBBox( box );

            Gizmo.Draw.LineThickness = 2;
            Gizmo.Draw.Color = Gizmo.Colors.Left;
            if ( box.Size.y > 0.01f )
                Gizmo.Draw.ScreenText( $"L: {box.Size.y:0.#}", box.Maxs.WithY( box.Center.y ), Vector2.Up * 32, size: textSize );
            Gizmo.Draw.Line( box.Maxs.WithY( box.Mins.y ), box.Maxs.WithY( box.Maxs.y ) );
            Gizmo.Draw.Color = Gizmo.Colors.Forward;
            if ( box.Size.x > 0.01f )
                Gizmo.Draw.ScreenText( $"W: {box.Size.x:0.#}", box.Maxs.WithX( box.Center.x ), Vector2.Up * 32, size: textSize );
            Gizmo.Draw.Line( box.Maxs.WithX( box.Mins.x ), box.Maxs.WithX( box.Maxs.x ) );
            Gizmo.Draw.Color = Gizmo.Colors.Up;
            if ( box.Size.z > 0.01f )
                Gizmo.Draw.ScreenText( $"H: {box.Size.z:0.#}", box.Maxs.WithZ( box.Center.z ), Vector2.Up * 32, size: textSize );
            Gizmo.Draw.Line( box.Maxs.WithZ( box.Mins.z ), box.Maxs.WithZ( box.Maxs.z ) );
        }
    }

    // Method to add one subdivision level to selected faces (max 5 levels)
    public void AddDivision()
    {
        if ( _subdivisionLevel >= 5 ) return;

        var facesToSubdivide = Selection.OfType<MeshFace>().ToList();
        if ( facesToSubdivide.Count == 0 ) return;

        var components = facesToSubdivide.GroupBy( f => f.Component );

        foreach ( var group in components )
        {
            var mesh = group.Key.Mesh;
            var faceHandles = group.Select( f => f.Handle ).ToArray();

            // Linear subdivision: Level 0->1 cut, Level 1->2 cuts, Level 2->3 cuts, etc.
            // This is much more manageable than exponential
            var cuts = _subdivisionLevel + 1;

            var newFaces = new List<FaceHandle>();
            mesh.QuadSliceFaces( faceHandles, cuts, cuts, 5.0f, newFaces );

            // Clear selection and select new faces
            foreach ( var item in Selection.OfType<MeshFace>().Where( x => x.Component == group.Key ).ToList() )
            {
                Selection.Remove( item );
            }
            foreach ( var newFace in newFaces )
            {
                Selection.Add( new MeshFace( group.Key, newFace ) );
            }
        }

        _subdivisionLevel++;
    }

    // Method to decrease subdivision level counter
    public void RemoveDivision()
    {
        if ( _subdivisionLevel > 0 )
        {
            _subdivisionLevel--;
        }
    }

    public int GetSubdivisionLevel() => _subdivisionLevel;

    public void ResetSubdivisionLevel() => _subdivisionLevel = 0;
}

