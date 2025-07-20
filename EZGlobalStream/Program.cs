using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public class Program
{
    private static string _videoFolder = string.Empty;
    private static bool _shuffle = false;
    private static bool _verbose = false;
    // On Linux, ffmpeg is typically in the path. On Windows, you might need to provide the full path
    // or add it to your system's PATH environment variable.
    private static string _ffmpegPath = "ffmpeg";
    private static CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

#if DEBUG
    static Program() { _verbose = true; }
#endif

    public static async Task Main(string[] args)
    {
#if DEBUG
        Console.WriteLine("[VERBOSE] Debug build detected. Verbose mode enabled.");
#endif
        // --- Step 0: Check for verbose flag (overrides debug if set) ---
        if (args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase) || a.Equals("-v", StringComparison.OrdinalIgnoreCase)))
        {
            _verbose = true;
            Console.WriteLine("Verbose mode enabled.");
        }

        Console.WriteLine("--- EZInfiniteYTLive Console ---");

        // --- Step 1: Get the folder containing videos ---
        while (true)
        {
            Console.Write("Enter the path to your video folder: ");
            _videoFolder = Console.ReadLine()?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(_videoFolder) && Directory.Exists(_videoFolder))
            {
                break;
            }
            Console.WriteLine("Invalid folder path. Please try again.");
        }

        // --- Step 2: Get the RTMP URL and Stream Key from the user ---
        Console.Write("Enter your RTMP URL: ");
        string rtmpUrl = Console.ReadLine()?.Trim() ?? string.Empty;

        Console.Write("Enter your Stream Key: ");
        string streamKey = Console.ReadLine()?.Trim() ?? string.Empty;

        while (string.IsNullOrEmpty(rtmpUrl) || string.IsNullOrEmpty(streamKey))
        {
            Console.WriteLine("RTMP URL and Stream Key cannot be empty. Please try again.");
            Console.Write("Enter your RTMP URL: ");
            rtmpUrl = Console.ReadLine()?.Trim() ?? string.Empty;

            Console.Write("Enter your Stream Key: ");
            streamKey = Console.ReadLine()?.Trim() ?? string.Empty;
        }

        // Combine the URL and key, handling the separator
        string fullRtmp = rtmpUrl.EndsWith("/") ? rtmpUrl + streamKey : $"{rtmpUrl}/{streamKey}";

        // --- Step 3: Choose between alphabetic and shuffle order ---
        while (true)
        {
            Console.Write("Stream in (1) Alphabetic Order or (2) Shuffle? Enter 1 or 2: ");
            string? choice = Console.ReadLine();
            if (choice == "1")
            {
                _shuffle = false;
                break;
            }
            if (choice == "2")
            {
                _shuffle = true;
                break;
            }
            Console.WriteLine("Invalid choice. Please enter 1 or 2.");
        }

        // --- Step 4: Start the stream and handle cancellation ---

        // This sets up a handler so that when you press Ctrl+C, the application
        // will cancel the streaming task instead of terminating abruptly.
        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            Console.WriteLine("\nCtrl+C detected. Stopping stream gracefully...");
            eventArgs.Cancel = true; // Prevents the process from terminating immediately
            _cancellationTokenSource.Cancel();
        };

        try
        {
            // The main streaming logic is now in its own async method
            await StreamVideosAsync(fullRtmp, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            // This is the expected exception when Ctrl+C is pressed.
            Console.WriteLine("Streaming was successfully stopped by the user.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An unexpected error occurred: {ex.Message}");
        }
        finally
        {
            Console.WriteLine("Application is shutting down.");
        }
    }

    private static void Verbose(string message)
    {
        if (_verbose)
        {
            Console.WriteLine($"[VERBOSE] {message}");
        }
    }

    /// <summary>
    /// This method contains the main streaming loop.
    /// It gets the list of videos, shuffles or sorts them, and then streams them one by one in a loop.
    /// </summary>
    private static async Task StreamVideosAsync(string rtmpUrl, CancellationToken cancellationToken)
    {
        Console.WriteLine("\nStarting stream... Press Ctrl+C to stop.");
        Verbose($"Entering streaming loop. Shuffle: {_shuffle}");

        while (!cancellationToken.IsCancellationRequested)
        {
            var videoFiles = Directory.EnumerateFiles(_videoFolder)
                .Where(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".flv", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Verbose($"Found {videoFiles.Count} video files.");
            if (!videoFiles.Any())
            {
                Console.WriteLine($"No video files found in {_videoFolder}. Please add videos and restart. Waiting for 30 seconds...");
                await Task.Delay(30000, cancellationToken);
                continue; // Re-check the folder after waiting
            }

            // Apply ordering based on user choice
            IEnumerable<string> playlist = _shuffle
                ? videoFiles.OrderBy(f => Guid.NewGuid()) // A simple and effective shuffle
                : videoFiles.OrderBy(f => f);

            Verbose($"Playlist: {string.Join(", ", playlist.Select(Path.GetFileName))}");

            foreach (var videoFile in playlist)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Verbose("Cancellation requested before next video.");
                    break;
                }

                Console.WriteLine($"\nNow streaming: {Path.GetFileName(videoFile)}");
                Verbose($"Starting ffmpeg for: {videoFile}");

                using (var process = new Process())
                {
                    process.StartInfo.FileName = _ffmpegPath;
                    process.StartInfo.Arguments = $"-re -i \"{videoFile}\" -c:v copy -c:a aac -ar 44100 -b:a 128k -f flv \"{rtmpUrl}\"";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;

                    try
                    {
                        process.Start();
                        Verbose($"ffmpeg started (PID: {process.Id})");

                        // Show ffmpeg output in verbose mode
                        Task? stdOutTask = null, stdErrTask = null;
                        if (_verbose)
                        {
                            stdOutTask = Task.Run(async () => {
                                string? line;
                                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                                {
                                    Console.WriteLine($"[ffmpeg:stdout] {line}");
                                }
                            });
                            stdErrTask = Task.Run(async () => {
                                string? line;
                                while ((line = await process.StandardError.ReadLineAsync()) != null)
                                {
                                    Console.WriteLine($"[ffmpeg:stderr] {line}");
                                }
                            });
                        }

                        await process.WaitForExitAsync(cancellationToken);
                        Verbose($"ffmpeg exited with code {process.ExitCode}");
                        if (_verbose)
                        {
                            if (stdOutTask != null) await stdOutTask;
                            if (stdErrTask != null) await stdErrTask;
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        Verbose("TaskCanceledException: Killing ffmpeg process.");
                        if (!process.HasExited)
                        {
                            process.Kill();
                            await process.WaitForExitAsync(CancellationToken.None);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (Exception ex)
                    {
                        Verbose($"Exception: {ex}");
                        Console.WriteLine($"Error streaming {Path.GetFileName(videoFile)}: {ex.Message}");
                    }
                }
                Verbose($"Finished streaming: {videoFile}");
            }
            Verbose("End of playlist loop. Restarting if not cancelled.");
            Verbose("Restarting playlist from the beginning.");
        }
    }
}