namespace ArturRios.Heimdall.WebApi;

public class Program
{
    public static void Main(string[] args)
    {
        var startup = new Startup(args);

        startup.BuildAndRun();
    }
}
