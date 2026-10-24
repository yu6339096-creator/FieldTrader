using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
internal static class InstalledLauncher {
    private static string Quote(string value) {
        return "\""+Regex.Replace(value,@"(\\*)\""",@"$1$1\""")+Regex.Match(value,@"\\*$").Value+"\"";
    }
    [STAThread] private static int Main(string[] args) {
        try {
            string root=AppDomain.CurrentDomain.BaseDirectory;
            string runtime=Path.Combine(root,"Runtime");
            string command=Quote(Path.Combine(root,"FieldTrader.dll"));
            foreach(string arg in args)command+=" "+Quote(arg);
            var start=new ProcessStartInfo(Path.Combine(runtime,"dotnet.exe"),command){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root};
            start.EnvironmentVariables["DOTNET_ROOT"]=runtime;
            start.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"]="0";
            using(var process=Process.Start(start)){process.WaitForExit();return process.ExitCode;}
        }catch(Exception ex){MessageBox.Show(ex.Message,"战地行商启动失败");return 1;}
    }
}
