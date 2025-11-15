using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

class StudentAgent
{
    static async Task Main(string[] args)
    {
        string studentId = Environment.UserName;
        string serverIP = "127.0.0.1";

        foreach (var arg in args)
        {
            if (arg.StartsWith("--id="))
                studentId = arg.Substring("--id=".Length).Trim();

            else if (arg.StartsWith("--server="))
                serverIP = arg.Substring("--server=".Length).Trim();

            else if (arg == "-i" || arg == "-ш")
            {
                int index = Array.IndexOf(args, arg);
                if (index >= 0 && index < args.Length - 1)
                    studentId = args[index + 1];
            }
            else if (arg == "-s")
            {
                int index = Array.IndexOf(args, arg);
                if (index >= 0 && index < args.Length - 1)
                    serverIP = args[index + 1];
            }
        }

        string serverUrl = $"ws://{serverIP}:8000/ws/{studentId}";
        Console.WriteLine($"Student ID: {studentId}");
        Console.WriteLine($"Server IP: {serverIP}");
        Console.WriteLine("=====================================");
        Console.WriteLine(" Waiting for server connection... ");
        Console.WriteLine("=====================================");

        while (true)
        {
            ClientWebSocket client = new ClientWebSocket();

            try
            {
                await client.ConnectAsync(new Uri(serverUrl), CancellationToken.None);
                Console.WriteLine($"Connected to server: {serverUrl}");

                // начать обработку сообщений
                await HandleConnection(client);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection error: {ex.Message}");
            }

            Console.WriteLine("Lost connection. Reconnecting in 5 seconds...");
            await Task.Delay(5000);
        }
    }

    static async Task HandleConnection(ClientWebSocket ws)
    {
        byte[] buffer = new byte[1024 * 1024];

        while (ws.State == WebSocketState.Open)
        {
            try
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult res;

                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        return;
                    }

                    ms.Write(buffer, 0, res.Count);

                } while (!res.EndOfMessage);

                string msg = Encoding.UTF8.GetString(ms.ToArray());

                JsonDocument doc;
                try { doc = JsonDocument.Parse(msg); }
                catch
                {
                    Console.WriteLine("Invalid JSON received (ignored)");
                    continue;
                }

                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeProp))
                {
                    string t = typeProp.GetString();

                    // Обработка ошибок сервера ДО проверки command_id
                    if (t == "error")
                    {
                        string msgText = root.GetProperty("message").GetString();
                        Console.WriteLine("Server error: " + msgText);

                        if (msgText == "client_id_already_used")
                        {
                            Console.WriteLine("This ID is already connected. Stopping client.");
                            Environment.Exit(0);
                        }

                        continue;
                    }
                }

                if (!root.TryGetProperty("command_id", out var cmdIDprop))
                    continue;

                string command_id = cmdIDprop.GetString();
                string type = root.GetProperty("type").GetString();

                //--------------------------------------------------------------------
                // COMMAND WRAPPER
                //--------------------------------------------------------------------
                if (type == "command")
                {
                    string command = root.GetProperty("command").GetString();

                    object result = ExecuteSimpleCommand(command);

                    await SendResponse(ws, command_id, "ok", result);
                }
                //--------------------------------------------------------------------
                // FILE UPLOAD CHUNK — FIXED
                //--------------------------------------------------------------------
                else if (type == "file_upload_chunk")
                {
                    string target = root.GetProperty("target_path").GetString();
                    string filename = root.GetProperty("filename").GetString();
                    int index = root.GetProperty("chunk_index").GetInt32();
                    int total = root.GetProperty("total_chunks").GetInt32();
                    string data = root.GetProperty("data").GetString();

                    Directory.CreateDirectory(target);

                    string tempFile = Path.Combine(target, filename + ".tmp");
                    byte[] bytes = Convert.FromBase64String(data);

                    using (var fs = new FileStream(tempFile, FileMode.Append, FileAccess.Write))
                        fs.Write(bytes, 0, bytes.Length);

                    Console.WriteLine($"Chunk {index + 1}/{total} received");

                    object resultObj;

                    if (index == total - 1)
                    {
                        string final = Path.Combine(target, filename);

                        if (File.Exists(final)) File.Delete(final);
                        File.Move(tempFile, final);

                        Console.WriteLine("File fully received, size = " + new FileInfo(final).Length);

                        if (Path.GetExtension(final).ToLower() == ".zip")
                        {
                            string extract = Path.Combine(target, Path.GetFileNameWithoutExtension(filename));
                            Console.WriteLine("Extracting ZIP...");

                            System.IO.Compression.ZipFile.ExtractToDirectory(final, extract, true);

                            File.Delete(final);

                            resultObj = $"Extracted to {extract}";
                        }
                        else
                        {
                            resultObj = $"Saved {final}";
                        }
                    }
                    else
                    {
                        resultObj = $"Chunk {index + 1}/{total} OK";
                    }

                    await SendResponse(ws, command_id, "ok", resultObj);
                }
                else
                {
                    await SendResponse(ws, command_id, "error", $"unknown type: {type}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex}");
                await Task.Delay(200);
            }
        }
    }

    static object ExecuteSimpleCommand(string command)
    {
        if (command == "get_desktop_path")
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        if (command.StartsWith("listdir:"))
        {
            string path = command.Substring(8);

            if (!Directory.Exists(path))
                return "Path not found";

            return Directory.GetFiles(path);
        }

        if (command.StartsWith("clean_dir:"))
        {
            string path = command.Substring(10);

            if (!Directory.Exists(path))
                return "Path not found";

            string[] allowedExtensions = { ".exe", ".lnk" };

            foreach (var f in Directory.GetFiles(path))
            {
                string ext = Path.GetExtension(f).ToLower();

                if (Array.Exists(allowedExtensions, e => e == ext))
                {
                    Console.WriteLine($"Skipped: {f}");
                    continue; // не удаляем exe и lnk
                }

                try
                {
                    File.Delete(f);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to delete file {f}: {ex.Message}");
                }
            }

            foreach (var d in Directory.GetDirectories(path))
            {
                try
                {
                    Directory.Delete(d, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to delete directory {d}: {ex.Message}");
                }
            }

            return "Cleaned (exe and lnk preserved)";
        }

        return $"Unknown command: {command}";
    }

    static async Task SendResponse(ClientWebSocket ws, string cmdid, string status, object result)
    {
        var resp = new
        {
            command_id = cmdid,
            status = status,
            result = result
        };

        string json = JsonSerializer.Serialize(resp);
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
