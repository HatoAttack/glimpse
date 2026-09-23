namespace ImageViewer.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // 引数にフォルダが渡されたら最初に開く（エクスプローラーの「送る」等から起動する用）
        string? folder = args.FirstOrDefault(Directory.Exists);
        Application.Run(new MainForm(folder));
    }
}
