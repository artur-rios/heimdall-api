namespace ArturRios.Heimdall.WebApi;

public class Program
{
    public static void Main(string[] args)
    {
        var app = new Startup(args).CreateApplication();

        app.Run();
    }
}
