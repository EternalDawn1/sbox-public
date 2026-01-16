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
				var brushModeButton = new Button( "Brush Mode", _tool.BrushModeEnabled ? "brush" : "pan_tool" );
				brushModeButton.Clicked = () =>
				{
					_tool.BrushModeEnabled = !_tool.BrushModeEnabled;
					brushModeButton.Icon = _tool.BrushModeEnabled ? "brush" : "pan_tool";
					brushModeButton.Text = _tool.BrushModeEnabled ? "Brush Mode" : "Select Mode";
				};
				brushModeButton.ToolTip = "Toggle between brush painting and face selection";
				brushRow.Add( brushModeButton );

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

				{
					var r = group.AddRow();
					r.Spacing = 4;
					r.Add( new Label( "Constrain to Selected" ) );
					r.Add( ControlWidget.Create( tool.GetSerialized().GetProperty( nameof( ConstrainToSelectedFaces ) ) ) );
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
				levelRow.Add( new Label( "Subdivision Level" ) );

				var levelControl = ControlWidget.Create( tool.GetSerialized().GetProperty( nameof( SubdivisionLevel ) ) );
				levelControl.Enabled = hasSelectedFaces;
				
				levelRow.Add( levelControl );
			}

			Layout.AddStretchCell();
		}

	}
}
