using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Diffra.Protocol;

namespace Diffra.Cli;

/// <summary>CLI commands for materializing presentation bundles and hosting local review servers.</summary>
internal static class PresentCommand
{
    private const string Usage = "Usage: diffra present <bundle|serve> [options]\n" +
        "  bundle --delta <delta.json> -o <output-dir> [--assessment <file>] [--baseline-ref <file>] [--manifest <file>] [--artifacts-dir <dir>] [--store <cas-path>]\n" +
        "  serve <bundle-directory> [--port <port>]";

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            Console.Error.WriteLine(Usage);
            return 0;
        }

        var subcommand = args[0];
        var remaining = args[1..];

        if (string.Equals(subcommand, "bundle", StringComparison.Ordinal))
        {
            return RunBundle(remaining);
        }

        if (string.Equals(subcommand, "serve", StringComparison.Ordinal))
        {
            return RunServe(remaining);
        }

        Console.Error.WriteLine($"Unknown present subcommand '{subcommand}'.");
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static int RunBundle(string[] args)
    {
        string? deltaPath = null;
        string? outputPath = null;
        string? assessmentPath = null;
        string? baselineRefPath = null;
        string? manifestPath = null;
        string? deliveryIndexPath = null;
        string? artifactsDir = null;
        string? storePath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var opt = args[i];
            if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
            {
                Console.Error.WriteLine($"Option '{opt}' requires a value.");
                return 2;
            }

            var val = args[++i];
            switch (opt)
            {
                case "--delta": deltaPath = val; break;
                case "-o": case "--output": outputPath = val; break;
                case "--assessment": assessmentPath = val; break;
                case "--baseline-ref": baselineRefPath = val; break;
                case "--manifest": manifestPath = val; break;
                case "--delivery-index": deliveryIndexPath = val; break;
                case "--artifacts-dir": artifactsDir = val; break;
                case "--store": storePath = val; break;
                default:
                    Console.Error.WriteLine($"Unknown bundle option '{opt}'.");
                    return 2;
            }
        }

        if (deltaPath is null || outputPath is null)
        {
            Console.Error.WriteLine("Both --delta and -o/--output are required.");
            return 2;
        }

        try
        {
            var options = new PresentationBundle.MaterializeOptions(
                outputPath,
                deltaPath,
                assessmentPath,
                baselineRefPath,
                manifestPath,
                deliveryIndexPath,
                artifactsDir,
                storePath);

            var res = PresentationBundle.Materialize(options);
            Console.WriteLine($"Successfully materialized review bundle in {res.OutputDirectory} ({res.DocumentsCount} documents, {res.AssetsCount} assets).");
            return 0;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Bundle creation failed: {ex.Message}");
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Bundle validation failed: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Bundle creation failed: {ex.Message}");
            return 1;
        }
    }

    public static int RunServe(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            Console.Error.WriteLine("The serve command requires a bundle directory path.");
            return 2;
        }

        var bundleDir = args[0];
        int port = 5055;

        for (var i = 1; i < args.Length; i++)
        {
            var opt = args[i];
            if (opt is "--port" or "-p")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out port) || port < 0 || port > 65535)
                {
                    Console.Error.WriteLine("Invalid port number specified.");
                    return 2;
                }
            }
        }

        var fullPath = Path.GetFullPath(bundleDir);
        if (!Directory.Exists(fullPath))
        {
            Console.Error.WriteLine($"Bundle directory not found: {fullPath}");
            return 2;
        }

        var indexHtml = Path.Combine(fullPath, "index.html");
        if (!File.Exists(indexHtml))
        {
            Console.Error.WriteLine($"Bundle directory does not contain index.html: {fullPath}");
            return 2;
        }

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = fullPath,
                WebRootPath = fullPath
            });

            builder.WebHost.UseKestrel(kestrel =>
            {
                kestrel.Listen(IPAddress.Loopback, port);
            });

            builder.Logging.ClearProviders();

            var app = builder.Build();

            // Strict CSP and Host Validation Middleware
            app.Use(async (context, next) =>
            {
                var host = context.Request.Host.Host;
                if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Invalid Host header.");
                    return;
                }

                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'self'; script-src 'none'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; frame-ancestors 'none';";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["X-Frame-Options"] = "DENY";

                await next();
            });

            var fileProvider = new PhysicalFileProvider(fullPath);
            app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = fileProvider,
                DefaultFileNames = new List<string> { "index.html" }
            });

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                ServeUnknownFileTypes = true,
                DefaultContentType = "application/octet-stream"
            });

            app.Start();

            var addresses = app.Urls;
            var address = addresses.FirstOrDefault() ?? $"http://127.0.0.1:{port}";
            Console.WriteLine($"Serving review bundle at {address}");
            Console.WriteLine("Press Ctrl+C to stop.");

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.WaitHandle.WaitOne();
                app.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                app.WaitForShutdown();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Serve failed: {ex.Message}");
            return 1;
        }
    }
}
