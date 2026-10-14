using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private void RenderReviewConfigurationEditor(ServerDefinition server)
    {
        var name=advancedEditorFile==0?"GameUserSettings.ini":"Game.ini";
        var loaded=managerRoot is null?advancedLoadedFiles.GetValueOrDefault((server.Id,advancedEditorFile))??new(server.Id,name,"Windows",null,[]):SettingsDraft.ReadPreview(managerRoot,server.Id,name);
        var body=new StackPanel {Spacing=14};body.Children.Add(EditorButton("‹  Back to Settings",()=>{advancedEditorServer=null;Render();}));body.Children.Add(Label(name,24,true));
        var editor=EditorText(loaded.Text??"");editor.AcceptsReturn=true;editor.TextWrapping=TextWrapping.NoWrap;editor.FontFamily=new FontFamily("Consolas");editor.Height=Math.Max(200,Bounds.Height-570);ScrollViewer.SetVerticalScrollBarVisibility(editor,ScrollBarVisibility.Auto);ScrollViewer.SetHorizontalScrollBarVisibility(editor,ScrollBarVisibility.Auto);body.Children.Add(editor);
        var notice=Label("Changes apply to the dummy’s own configuration files.",18);body.Children.Add(notice);
        body.Children.Add(EditorButton("Save Changes",()=>_ = SaveAsync(),true));content.Children.Add(OverviewFrame(body));
        async Task SaveAsync()
        {
            try
            {
                var text=SettingsDraft.Apply(editor.Text??"",[]);
                if(managerRoot is not null)await SettingsDraft.SavePreviewAsync(managerRoot,loaded,text,lifetime.Token);
                loaded=managerRoot is null?loaded with {Text=text}:SettingsDraft.ReadPreview(managerRoot,server.Id,name);
                advancedLoadedFiles[(server.Id,advancedEditorFile)]=loaded;reviewSavedSettings.Clear();notice.Text="Configuration saved.";
            }
            catch(Exception e)when(e is IOException or ArgumentException or OperationCanceledException){notice.Text=e.Message;}
        }
    }
}
