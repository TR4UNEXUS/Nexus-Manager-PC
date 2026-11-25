using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Nexus.Desktop;

internal static class NexusTheme
{
    public static readonly Color Background=Color.FromArgb(15,19,32), Surface=Color.FromArgb(20,27,44),
        Alternate=Color.FromArgb(24,32,52), Header=Color.FromArgb(30,40,63), Edge=Color.FromArgb(51,64,92),
        Text=Color.FromArgb(239,244,255), Secondary=Color.FromArgb(178,194,219),
        Accent=Color.FromArgb(123,133,255), Cyan=Color.FromArgb(96,194,253), Violet=Color.FromArgb(164,117,248),
        Selection=Color.FromArgb(54,58,104), Good=Color.FromArgb(149,217,204), Warning=Color.FromArgb(255,205,135);
    public static bool Enabled=>!SystemInformation.HighContrast;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr h,int attribute,ref int value,int size);
    [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr window,string? theme,string? subId);

    public static void TitleBar(Form window){
        if(!Enabled||!OperatingSystem.IsWindowsVersionAtLeast(10))return;
        int dark=1;DwmSetWindowAttribute(window.Handle,20,ref dark,sizeof(int));
    }
    public static void NativeDark(Control control){
        if(Enabled)control.HandleCreated+=(_,_)=>SetWindowTheme(control.Handle,"DarkMode_Explorer",null);
    }
    public static void ButtonStyle(Button b,bool active=false){
        if(!Enabled){b.FlatStyle=FlatStyle.System;return;}
        b.FlatStyle=FlatStyle.Flat;b.UseVisualStyleBackColor=false;b.ForeColor=Text;
        b.BackColor=active?Selection:Header;b.FlatAppearance.BorderColor=active?Accent:Edge;
        b.FlatAppearance.MouseOverBackColor=Selection;b.FlatAppearance.MouseDownBackColor=Color.FromArgb(66,66,118);
    }
    public static void MenuStyle(MenuStrip menu){
        if(!Enabled)return;
        menu.BackColor=Background;menu.ForeColor=Text;menu.Renderer=new MenuPaint();
        foreach(var item in menu.Items.OfType<ToolStripMenuItem>()){
            item.ForeColor=Text;item.DropDown.BackColor=Surface;
            foreach(ToolStripItem action in item.DropDownItems)action.ForeColor=Text;
        }
    }
    sealed class MenuPaint:ToolStripProfessionalRenderer{
        public MenuPaint():base(new MenuColors()){RoundedEdges=false;}
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e){
            if(e.ToolStrip is not MenuStrip)base.OnRenderToolStripBorder(e);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e){
            e.TextColor=e.Item.Enabled?Text:Secondary;base.OnRenderItemText(e);
        }
    }
    sealed class MenuColors:ProfessionalColorTable{
        public override Color ToolStripDropDownBackground=>Surface;
        public override Color ImageMarginGradientBegin=>Surface;
        public override Color ImageMarginGradientMiddle=>Surface;
        public override Color ImageMarginGradientEnd=>Surface;
        public override Color MenuBorder=>Edge;
        public override Color MenuItemBorder=>Accent;
        public override Color MenuItemSelected=>Selection;
        public override Color MenuItemSelectedGradientBegin=>Selection;
        public override Color MenuItemSelectedGradientEnd=>Selection;
        public override Color MenuItemPressedGradientBegin=>Header;
        public override Color MenuItemPressedGradientMiddle=>Header;
        public override Color MenuItemPressedGradientEnd=>Header;
    }
}

sealed class WorkspaceCommand:Button
{
    bool hovering, pressed;
    protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hovering=true;Invalidate();}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hovering=pressed=false;Invalidate();}
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);pressed=true;Invalidate();}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);pressed=false;Invalidate();}
    protected override void OnPaint(PaintEventArgs e){
        if(!NexusTheme.Enabled || Width<4 || Height<4){base.OnPaint(e);return;}
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor??NexusTheme.Background);
        float scale=DeviceDpi/96f;
        using var path=WorkspaceArt.Rounded(new(1,1,Width-3,Height-3),6*scale);
        using var fill=new SolidBrush(!Enabled?NexusTheme.Surface:pressed?NexusTheme.Selection:hovering?NexusTheme.Alternate:NexusTheme.Header);
        using var edge=new Pen(Enabled&&hovering?NexusTheme.Accent:NexusTheme.Edge);
        g.FillPath(fill,path);g.DrawPath(edge,path);
        TextRenderer.DrawText(g,Text,Font,ClientRectangle,Enabled?NexusTheme.Text:NexusTheme.Secondary,
            TextFormatFlags.VerticalCenter|TextFormatFlags.HorizontalCenter|TextFormatFlags.SingleLine|
            (ShowKeyboardCues?0:TextFormatFlags.HidePrefix));
        if(Focused && ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-4,-4),NexusTheme.Text,fill.Color);
    }
}

// The native details control still owns scrolling, keyboard navigation,
// column resizing, sorting events and accessibility.
sealed class NexusTable:ListView
{
    public NexusTable(){
        DoubleBuffered=true;
        if(!NexusTheme.Enabled)return;
        OwnerDraw=true;BackColor=NexusTheme.Surface;ForeColor=NexusTheme.Text;
        BorderStyle=BorderStyle.None;NexusTheme.NativeDark(this);
    }
    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e){
        if(!OwnerDraw){base.OnDrawColumnHeader(e);return;}
        using var background=new SolidBrush(NexusTheme.Header);
        using var rule=new Pen(NexusTheme.Edge);
        e.Graphics.FillRectangle(background,e.Bounds);
        e.Graphics.DrawLine(rule,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
        var text=e.Bounds;text.X+=8;text.Width=Math.Max(0,text.Width-12);
        TextRenderer.DrawText(e.Graphics,e.Header?.Text,Font,text,NexusTheme.Secondary,Flags);
    }
    const TextFormatFlags Flags=TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|
        TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine;
    protected override void OnDrawItem(DrawListViewItemEventArgs e){if(View!=View.Details)e.DrawDefault=true;}
    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e){
        if(e.Item==null||e.SubItem==null)return;
        bool selected=e.Item.Selected;
        var fill=selected?NexusTheme.Selection:e.ItemIndex%2==0?NexusTheme.Surface:NexusTheme.Alternate;
        using var brush=new SolidBrush(fill);e.Graphics.FillRectangle(brush,e.Bounds);
        var text=e.Bounds;text.X+=8;text.Width=Math.Max(0,text.Width-12);
        if(e.ColumnIndex==0){
            if(selected){using var marker=new SolidBrush(NexusTheme.Accent);e.Graphics.FillRectangle(marker,e.Bounds.Left,e.Bounds.Top,3,e.Bounds.Height);}
            if(SmallImageList is {} icons && e.Item.ImageIndex>=0 && e.Item.ImageIndex<icons.Images.Count){
                var size=icons.ImageSize;
                icons.Draw(e.Graphics,text.X,e.Bounds.Top+(e.Bounds.Height-size.Height)/2,e.Item.ImageIndex);
                text.X+=size.Width+7;text.Width=Math.Max(0,text.Width-size.Width-7);
            }
        }
        var color=e.SubItem.Text=="Files present"?NexusTheme.Good:e.SubItem.Text.StartsWith("Needs checking")||e.SubItem.Text.Contains("file issue")?NexusTheme.Warning:NexusTheme.Text;
        TextRenderer.DrawText(e.Graphics,e.SubItem.Text,Font,text,color,Flags);
    }
}
