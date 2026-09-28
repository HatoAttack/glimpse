using ImageViewer.Core.Navigation;

namespace ImageViewer.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // 引数にフォルダ（ZIP も）が渡されたら最初に開く（エクスプローラーの「送る」「プログラムから開く」等から起動する用）
        string? folder = args.FirstOrDefault(FolderListing.CanOpen);
        Application.Run(new MainForm(folder));
    }
}
