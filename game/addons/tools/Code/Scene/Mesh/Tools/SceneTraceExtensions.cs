
namespace Editor.MeshEditor;

static class SceneTraceMeshExtensions
{
	static Vector2 RayScreenPosition => SceneViewportWidget.MousePosition;

	public static MeshVertex GetClosestVertex( this SceneTrace trace, int radius )
	{
		var point = RayScreenPosition;
		var bestFace = TraceFace( trace, out var bestHitDistance );
		var bestVertex = bestFace.GetClosestVertex( point, radius );

		if ( bestFace.IsValid() && bestVertex.IsValid() )
			return bestVertex;

		var results = TraceFaces( trace, radius, point );
		foreach ( var result in results )
		{
			var face = result.MeshFace;
			var hitDistance = result.Distance;
			var vertex = face.GetClosestVertex( point, radius );
			if ( !vertex.IsValid() )
				continue;

			if ( hitDistance < bestHitDistance || !bestFace.IsValid() )
			{
				bestHitDistance = hitDistance;
				bestVertex = vertex;
				bestFace = face;
			}
		}

		return bestVertex;
	}

	public static MeshEdge GetClosestEdge( this SceneTrace trace, int radius )
	{
		var point = RayScreenPosition;
		var bestFace = TraceFace( trace, out var bestHitDistance );
		var hitPosition = Gizmo.CurrentRay.Project( bestHitDistance );
		var bestEdge = bestFace.GetClosestEdge( hitPosition, point, radius );

		if ( bestFace.IsValid() && bestEdge.IsValid() )
			return bestEdge;

		var results = TraceFaces( trace, radius, point );
		foreach ( var result in results )
		{
			var face = result.MeshFace;
			var hitDistance = result.Distance;
			hitPosition = Gizmo.CurrentRay.Project( hitDistance );

			var edge = face.GetClosestEdge( hitPosition, point, radius );
			if ( !edge.IsValid() )
				continue;

			if ( hitDistance < bestHitDistance || !bestFace.IsValid() )
			{
				bestHitDistance = hitDistance;
				bestEdge = edge;
				bestFace = face;
			}
		}

		return bestEdge;
	}

	static MeshFace TraceFace( this SceneTrace trace, out float distance )
	{
		distance = default;

		var result = trace.Run();
		if ( !result.Hit || result.Component is not MeshComponent component )
			return default;

		distance = result.Distance;
		var face = component.Mesh.TriangleToFace( result.Triangle );
		return new MeshFace( component, face );
	}

	struct MeshFaceTraceResult
	{
		public MeshFace MeshFace;
		public float Distance;
	}

	static List<MeshFaceTraceResult> TraceFaces( this SceneTrace trace, int radius, Vector2 point )
	{
		var rays = new List<Ray> { Gizmo.CurrentRay };
		for ( var ring = 1; ring < radius; ring++ )
		{
			rays.Add( Gizmo.Camera.GetRay( point + new Vector2( 0, ring ) ) );
			rays.Add( Gizmo.Camera.GetRay( point + new Vector2( ring, 0 ) ) );
			rays.Add( Gizmo.Camera.GetRay( point + new Vector2( 0, -ring ) ) );
			rays.Add( Gizmo.Camera.GetRay( point + new Vector2( -ring, 0 ) ) );
		}

		var faces = new List<MeshFaceTraceResult>();
		var faceHash = new HashSet<MeshFace>();
		foreach ( var ray in rays )
		{
			var result = trace.Ray( ray, Gizmo.RayDepth ).Run();
			if ( !result.Hit )
				continue;

			if ( result.Component is not MeshComponent component )
				continue;

			var face = component.Mesh.TriangleToFace( result.Triangle );
			var faceElement = new MeshFace( component, face );
			if ( faceHash.Add( faceElement ) )
				faces.Add( new MeshFaceTraceResult { MeshFace = faceElement, Distance = result.Distance } );
		}

		return faces;
	}
}
