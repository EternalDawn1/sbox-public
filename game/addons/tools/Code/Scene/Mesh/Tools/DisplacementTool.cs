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
        PushPull
    }

    public enum SubdivisionLevelEnum
    {
        [Title("Level 0")]
        Level0,
        [Title("Level 1")]
        Level1,
        [Title("Level 2")]
        Level2,
        [Title("Level 3")]
        Level3,
        [Title("Level 4")]
        Level4
    }

    [Property]
    public PaintMode Mode { get; set; } = PaintMode.PushPull;

    [Property, Range( 50f, 200.0f )]
    public float BrushRadius { get; set; } = 50f;

    [Property, Range( 1f, 10.0f )]
    public float BrushStrength { get; set; } = 1f;

    [Property]
    public SubdivisionLevelEnum SubdivisionLevel { get; set; } = SubdivisionLevelEnum.Level0;

    private MeshFace _hoverFace;
    private SceneDynamicObject _faceObject;
    private bool _isPainting = false;
    private Vector3 _lastPaintPosition;
    private IDisposable _undoScope;
    private Dictionary<MeshComponent, Dictionary<VertexHandle, Vector3>> _originalPositions = new();
    private Dictionary<MeshComponent, Dictionary<FaceHandle, int>> _faceSubdivisionLevels = new();

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
                // Nur Hover-Face für Visualisierung setzen, keine Selektion
                _hoverFace = TraceFace();

                // Selektion nur beim Klicken
                if ( Gizmo.WasLeftMousePressed )
                {
                    var face = _hoverFace;
                    if ( face.IsValid() )
                    {
                        var mesh = face.Component.Mesh;
                        var faceCount = mesh.FaceHandles.Count();
                        if ( faceCount > 6 ) // Displacement mesh
                        {
                            if ( !Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Shift ) )
                                Selection.Clear();

                            // Get normal of the clicked face
                            mesh.ComputeFaceNormal( face.Handle, out var clickedNormal );
                            // Select all faces with the same normal
                            foreach ( var handle in mesh.FaceHandles )
                            {
                                mesh.ComputeFaceNormal( handle, out var normal );
                                if ( normal.Normal == clickedNormal.Normal ) // Compare normals approximately
                                {
                                    Selection.Add( new MeshFace( face.Component, handle ) );
                                }
                            }
                        }
                        else
                        {
                            UpdateSelection( face );
                        }
                    }
                    else
                    {
                        UpdateSelection( face );
                    }
                }

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

        // Draw brush gizmo if brush mode is enabled
        if ( BrushModeEnabled )
        {
            DrawBrushGizmo();
        }

        // Sync subdivision level
        if ( (int)SubdivisionLevel != GetSubdivisionLevel() )
        {
            SetSubdivisionLevel( (int)SubdivisionLevel );
        }
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
        var groups = Selection.OfType<MeshFace>().GroupBy(f => GetFaceLevel(f)).Where(g => g.Key < 5);

        foreach (var group in groups)
        {
            var level = group.Key;
            var faces = group.ToList();
            var components = faces.GroupBy(f => f.Component);

            foreach (var compGroup in components)
            {
                var mesh = compGroup.Key.Mesh;
                var faceHandles = compGroup.Select(f => f.Handle).ToArray();
                var cuts = level + 1;

                var newFaces = new List<FaceHandle>();
                mesh.QuadSliceFaces(faceHandles, cuts, cuts, 5.0f, newFaces);

                // Set level for new faces
                foreach (var newFace in newFaces)
                {
                    SetFaceLevel(new MeshFace(compGroup.Key, newFace), level + 1);
                }

                // Remove old faces from selection
                foreach (var oldFace in compGroup)
                {
                    Selection.Remove(oldFace);
                }

                // Add new faces
                foreach (var newFace in newFaces)
                {
                    Selection.Add(new MeshFace(compGroup.Key, newFace));
                }
            }
        }
    }

    // Method to decrease subdivision level counter
    public void RemoveDivision()
    {
        foreach (var face in Selection.OfType<MeshFace>())
        {
            var level = GetFaceLevel(face);
            if (level > 0)
            {
                SetFaceLevel(face, level - 1);
            }
        }
    }

    public int GetSubdivisionLevel() => Selection.OfType<MeshFace>().Select(f => GetFaceLevel(f)).FirstOrDefault();

    public void SetSubdivisionLevel(int level)
    {
        foreach (var face in Selection.OfType<MeshFace>())
        {
            SetFaceLevel(face, level);
        }
    }

    public void ResetSubdivisionLevel() 
    {
        foreach (var face in Selection.OfType<MeshFace>())
        {
            SetFaceLevel(face, 0);
        }
    }

    private void DrawBrushGizmo()
    {
        using var scope = Gizmo.Scope( "Brush Gizmo" );

        Gizmo.Draw.IgnoreDepth = true;

        // Get the position under the cursor
        var result = MeshTrace.Run();
        var origin = result.Hit ? result.EndPosition : Vector3.Zero;

        // Outer circle for radius
        Gizmo.Draw.Color = Color.Cyan.WithAlpha( 0.5f );
        Gizmo.Draw.LineThickness = 2;
        const int segments = 32;
        for ( int i = 0; i < segments; i++ )
        {
            var angle1 = (float)i / segments * MathF.PI * 2;
            var angle2 = (float)(i + 1) / segments * MathF.PI * 2;

            var p1 = origin + new Vector3( MathF.Cos( angle1 ) * BrushRadius, MathF.Sin( angle1 ) * BrushRadius, 0 );
            var p2 = origin + new Vector3( MathF.Cos( angle2 ) * BrushRadius, MathF.Sin( angle2 ) * BrushRadius, 0 );

            Gizmo.Draw.Line( p1, p2 );
        }

        // Inner circle for strength
        Gizmo.Draw.Color = Color.White.WithAlpha( 1.0f );
        Gizmo.Draw.LineThickness = 2;
        var innerRadius = Math.Max( 1.0f, BrushRadius * BrushStrength );
        for ( int i = 0; i < segments; i++ )
        {
            var angle1 = (float)i / segments * MathF.PI * 2;
            var angle2 = (float)(i + 1) / segments * MathF.PI * 2;

            var p1 = origin + new Vector3( MathF.Cos( angle1 ) * innerRadius, MathF.Sin( angle1 ) * innerRadius, 0 );
            var p2 = origin + new Vector3( MathF.Cos( angle2 ) * innerRadius, MathF.Sin( angle2 ) * innerRadius, 0 );

            Gizmo.Draw.Line( p1, p2 );
        }
    }

    private BBox GetFaceBounds( PolygonMesh mesh, FaceHandle face )
    {
        var vertices = mesh.GetFaceVertices( face );
        var positions = vertices.Select( v => mesh.GetVertexPosition( v ) );
        return BBox.FromPoints( positions );
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

    private int GetFaceLevel(MeshFace face)
    {
        return _faceSubdivisionLevels.GetValueOrDefault(face.Component)?.GetValueOrDefault(face.Handle) ?? 0;
    }

    private void SetFaceLevel(MeshFace face, int level)
    {
        if (!_faceSubdivisionLevels.ContainsKey(face.Component))
            _faceSubdivisionLevels[face.Component] = new Dictionary<FaceHandle, int>();
        _faceSubdivisionLevels[face.Component][face.Handle] = level;
    }
}

