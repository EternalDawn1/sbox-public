
namespace Editor.MeshEditor;

partial class DisplacementTool
{
	public override Widget CreateToolSidebar()
	{
		return new DisplacementToolWidget( GetSerializedSelection(), this );
	}

	public class DisplacementToolWidget : ToolSidebarWidget
	{
		private readonly MeshFace[] _faces;
		private readonly List<IGrouping<MeshComponent, MeshFace>> _faceGroups;
		private readonly List<MeshComponent> _components;
		private readonly DisplacementTool _tool;

		public DisplacementToolWidget( SerializedObject so, DisplacementTool tool ) : base()
		{
			AddTitle( "Displacement Tool", "terrain" );

			_tool = tool;
			_faces = so.Targets
				.OfType<MeshFace>()
				.ToArray();

			_faceGroups = _faces.GroupBy( x => x.Component ).ToList();
			_components = _faceGroups.Select( x => x.Key ).ToList();

			bool hasSelectedFaces = _faces.Length > 0;

			{
				var group = AddGroup( "Mode" );
				
				var brushRow = group.AddRow();
				brushRow.Spacing = 4;
				var brushModeControl = ControlWidget.Create( tool.GetSerialized().GetProperty( nameof( BrushModeEnabled ) ) );
				brushModeControl.ToolTip = "Enable to paint displacement with brush, disable to select faces";
				brushRow.Add( new Label( "Brush Mode" ) );
				brushRow.Add( brushModeControl );

				var applyRow = group.AddRow();
				applyRow.Spacing = 4;
				var applyModeControl = ControlWidget.Create( tool.GetSerialized().GetProperty( nameof( Apply ) ) );
				applyModeControl.ToolTip = "Selected Only: Paint only on selected faces\nAll Displacements: Paint on all subdivided faces";
				applyRow.Add( new Label( "Apply Mode" ) );
				applyRow.Add( applyModeControl );
			}

			{
				var group = AddGroup( "Brush Settings" );

				{
					var r = group.AddRow();
					r.Spacing = 4;
					r.Add( new IconLabel( "radio_button_unchecked" ) );
					r.Add( ControlWidget.Create( tool.GetSerialized().GetProperty( nameof( BrushRadius ) ) ) );
				}

				{
					var r = group.AddRow();
					r.Spacing = 4;
					r.Add( new IconLabel( "tune" ) );
					r.Add( ControlWidget.Create( tool.GetSerialized().GetProperty( nameof( BrushStrength ) ) ) );
				}

				{
					var r = group.AddRow();
					r.Spacing = 4;
					r.Add( new IconLabel( "brush" ) );
					r.Add( ControlWidget.Create( tool.GetSerialized().GetProperty( nameof( Mode ) ) ) );
				}

				// Add hint about Ctrl modifier
				var hintRow = group.AddRow();
				hintRow.Spacing = 4;
				var hintLabel = new Label( "Hold CTRL to invert brush direction" );
				hintRow.Add( hintLabel );
			}

			{
				var group = AddGroup( "Subdivision" );

				var levelRow = group.AddRow();
				levelRow.Spacing = 4;
				var levelLabel = new Label( $"Level: {tool.GetSubdivisionLevel()} / 5" ) { FixedWidth = 100 };
				levelRow.Add( levelLabel );

				var buttonRow = group.AddRow();
				buttonRow.Spacing = 4;

				var addButton = new Button( "Add Division", "add" );
				addButton.Clicked = () =>
				{
					using var scope = SceneEditorSession.Scope();
					using ( SceneEditorSession.Active.UndoScope( "Add Division" )
						.WithComponentChanges( _components )
						.Push() )
					{
						tool.AddDivision();
						levelLabel.Text = $"Level: {tool.GetSubdivisionLevel()} / 5";
					}
				};
				addButton.Enabled = hasSelectedFaces && tool.GetSubdivisionLevel() < 5;
				addButton.ToolTip = "Add one subdivision level (doubles faces)";
				buttonRow.Add( addButton );

				var removeButton = new Button( "Remove Division", "remove" );
				removeButton.Clicked = () =>
				{
					if ( tool.GetSubdivisionLevel() > 0 )
					{
						// Use undo to restore previous geometry state
						SceneEditorSession.Active.UndoSystem.Undo();
						tool.RemoveDivision();
						levelLabel.Text = $"Level: {tool.GetSubdivisionLevel()} / 5";
					}
				};
				removeButton.Enabled = tool.GetSubdivisionLevel() > 0;
				removeButton.ToolTip = "Undo last subdivision (restores previous geometry)";
				buttonRow.Add( removeButton );

				var resetButton = new IconButton( "restart_alt", () =>
				{
					tool.ResetSubdivisionLevel();
					levelLabel.Text = $"Level: {tool.GetSubdivisionLevel()} / 5";
				} )
				{
					ToolTip = "Reset subdivision level to 0"
				};
				buttonRow.Add( resetButton );
			}

			Layout.AddStretchCell();
		}

		[Shortcut( "editor.delete", "DEL", typeof( SceneDock ) )]
		private void DeleteSelection()
		{
			var groups = _faces.GroupBy( face => face.Component );

			if ( !groups.Any() )
				return;

			var components = groups.Select( x => x.Key ).ToArray();

			using ( SceneEditorSession.Active.UndoScope( "Delete Faces" ).WithComponentChanges( components ).Push() )
			{
				foreach ( var group in groups )
					group.Key.Mesh.RemoveFaces( group.Select( x => x.Handle ) );
			}
		}
	}
}
