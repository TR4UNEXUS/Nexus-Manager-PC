using System.Drawing.Drawing2D;

namespace Nexus.Desktop;

internal sealed class OperationProgressBar : Control
{
    readonly System.Windows.Forms.Timer animation=new(){Interval=45};
    double? fraction;
    public bool Completed=>fraction==1;

    public OperationProgressBar(){
        Dock=DockStyle.Fill;DoubleBuffered=true;ResizeRedraw=true;Visible=false;
        AccessibleName="Overall progress";AccessibleRole=AccessibleRole.ProgressBar;
        Height=24;Margin=new Padding(2,1,2,2);
        animation.Tick+=(_,_)=>Invalidate();
        VisibleChanged+=(_,_)=>animation.Enabled=Visible&&!fraction.HasValue;
    }
    public void Begin(){
        fraction=null;Visible=true;animation.Enabled=Visible;AccessibleDescription="Working";Invalidate();
    }
    public void Report(double? value){
        if(!value.HasValue||!double.IsFinite(value.Value))return;
        fraction=Math.Clamp(value.Value,fraction??0,1);animation.Stop();
        AccessibleDescription=$"{(int)(fraction*100)} percent";Invalidate();
    }
    public void Clear(){
        animation.Stop();fraction=null;Visible=false;Invalidate();
    }
    protected override void Dispose(bool disposing){if(disposing)animation.Dispose();base.Dispose(disposing);}
    protected override void OnPaint(PaintEventArgs e){
        base.OnPaint(e);
        var r=ClientRectangle;
        int caption=LogicalToDeviceUnits(59);
        var track=new Rectangle(1,r.Height/4,Math.Max(0,r.Width-caption-2),Math.Max(1,r.Height/2));
        if(track.Width<1)return;
        bool themed=NexusTheme.Enabled;
        using var background=new SolidBrush(themed?NexusTheme.Header:SystemColors.ControlDark);
        e.Graphics.FillRectangle(background,track);
        var fill=track;
        if(fraction is double value)fill.Width=(int)Math.Round(track.Width*value);
        else{
            int width=Math.Max(1,track.Width/5);
            double wave=(Environment.TickCount64%1800)/900.0;
            fill.X+=(int)((track.Width-width)*(1-Math.Abs(1-wave)));fill.Width=width;
        }
        if(fill.Width>0){
            using var color=new LinearGradientBrush(track,themed?NexusTheme.Cyan:SystemColors.Highlight,
                themed?NexusTheme.Violet:SystemColors.Highlight,0f);
            e.Graphics.FillRectangle(color,fill);
        }
        string label=fraction is double f?$"{(int)(100*f)}%":"Working";
        TextRenderer.DrawText(e.Graphics,label,Font,new Rectangle(track.Right+2,0,caption,r.Height),
            themed?NexusTheme.Text:SystemColors.ControlText,TextFormatFlags.Right|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
    }
}
