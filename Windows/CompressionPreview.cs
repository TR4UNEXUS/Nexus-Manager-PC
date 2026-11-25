namespace Nexus.Desktop;

internal static class CompressionPreview
{
    public static void ShowResults(IWin32Window owner,IReadOnlyList<CompressionEstimate> games)
    {
        using var window=new Form{Text="Compression scan",StartPosition=FormStartPosition.CenterParent,
            MinimizeBox=false,ShowInTaskbar=false,ClientSize=new Size(770,Math.Clamp(145+games.Count*26,205,525)),
            MinimumSize=new Size(580,235),AutoScaleMode=AutoScaleMode.Dpi};
        var panel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(12)};
        panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var table=new NexusTable{View=View.Details,Dock=DockStyle.Fill,FullRowSelect=true,AccessibleName="Compression estimates"};
        foreach(var column in new[]{"Game","Current size","Estimated size","Estimated saving"})table.Columns.Add(column);
        foreach(var g in games)table.Items.Add(new ListViewItem([g.BaseOnly?g.Name+" (base only)":g.Name,CompressionEstimate.Size(g.BeforeBytes),
            CompressionEstimate.Size(g.AfterBytes),g.Savings]));
        void ResizeColumns(){
            int unit(int width)=>table.LogicalToDeviceUnits(width);
            table.Columns[1].Width=unit(95);table.Columns[2].Width=unit(110);table.Columns[3].Width=unit(175);
            table.Columns[0].Width=Math.Max(unit(125),table.ClientSize.Width-unit(380)-SystemInformation.VerticalScrollBarWidth);
        }
        table.Resize+=(_,_)=>ResizeColumns();
        panel.Controls.Add(table,0,0);
        var note=new Label{Text=games.Any(g=>g.BaseOnly)?"Base-only figures cover just the base game. Manager needs a matching permission backup to estimate its update or DLC.":"Sizes include the game, its selected update and DLC. Shared files can affect how much space the drive frees.",
            AutoSize=true,Dock=DockStyle.Fill,Padding=new Padding(0,8,0,5)};
        panel.Controls.Add(note,0,1);
        var close=LibraryWindow.ButtonFor("Close",window.Close);close.Anchor=AnchorStyles.Right;
        close.DialogResult=DialogResult.OK;panel.Controls.Add(close,0,2);window.AcceptButton=window.CancelButton=close;
        if(NexusTheme.Enabled){window.BackColor=NexusTheme.Background;window.ForeColor=NexusTheme.Text;note.ForeColor=NexusTheme.Secondary;}
        window.Controls.Add(panel);window.ShowDialog(owner);
    }
}
