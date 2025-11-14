using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
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

        while (true) // бесконечный цикл-страж
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

    static async Task HandleConnection(ClientWebSocket client)
    {
        var buffer = new byte[8192];
        StringBuilder fileBuffer = new();
        string currentPath = null, currentName = null;
        bool receivingFile = false;

        while (client.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;

            try
            {
                result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            }
            catch
            {
                // разрыв соединения
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);

            // ✨ — твоя логика обработки команд ниже (оставь без изменений)
            //-------------------------------------------------------------
            if (msg.StartsWith("file_start"))
            {
                string[] parts = msg.Split(new string[] { "|||" }, StringSplitOptions.None);
                currentPath = parts[1];
                currentName = parts[2];
                fileBuffer.Clear();
                receivingFile = true;
                Console.WriteLine($"Receiving file {currentName} to {currentPath}...");
            }
            else if (msg.StartsWith("file_data:") && receivingFile)
            {
                fileBuffer.Append(msg.Substring(10));
            }
            else if (msg == "file_end" && receivingFile)
            {
                try
                {
                    byte[] data = Convert.FromBase64String(fileBuffer.ToString());
                    Directory.CreateDirectory(currentPath);
                    string fullPath = Path.Combine(currentPath, currentName);
                    File.WriteAllBytes(fullPath, data);
                    await SendText(client, $"File '{currentName}' saved to {currentPath}");
                    Console.WriteLine($"Saved: {fullPath}");
                }
                catch (Exception ex)
                {
                    await SendText(client, $"Error saving file: {ex.Message}");
                }
                finally
                {
                    receivingFile = false;
                    fileBuffer.Clear();
                }
            }
            else if (msg == "get_desktop_path")
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                await SendText(client, desktop);
                Console.WriteLine($"Desktop path sent: {desktop}");
            }
            else if (msg.StartsWith("listdir:"))
            {
                string path = msg.Substring(8).Trim();
                try
                {
                    if (Directory.Exists(path))
                    {
                        var dirs = Directory.GetDirectories(path);
                        var files = Directory.GetFiles(path);

                        var json = new StringBuilder();
                        json.Append("{");
                        json.Append("\"folders\":[");
                        json.Append(string.Join(",", Array.ConvertAll(dirs, d => $"\"{d.Replace("\\", "\\\\")}\"")));
                        json.Append("],");
                        json.Append("\"files\":[");
                        json.Append(string.Join(",", Array.ConvertAll(files, f => $"\"{f.Replace("\\", "\\\\")}\"")));
                        json.Append("]");
                        json.Append("}");

                        await SendText(client, json.ToString());
                        Console.WriteLine($"Listed {path}");
                    }
                    else
                    {
                        await SendText(client, $"{{\"error\":\"Path not found: {path}\"}}");
                    }
                }
                catch (Exception ex)
                {
                    await SendText(client, $"{{\"error\":\"{ex.Message}\"}}");
                }
            }
            else if (msg.StartsWith("zip_start"))
            {
                string[] parts = msg.Split(new string[] { "|||" }, StringSplitOptions.None);
                currentPath = parts[1];
                currentName = parts[2];
                fileBuffer.Clear();
                receivingFile = true;
                Console.WriteLine($"Receiving ZIP {currentName} to {currentPath}...");
            }
            else if (msg.StartsWith("zip_data:") && receivingFile)
            {
                fileBuffer.Append(msg.Substring(9));
            }
            else if (msg == "zip_end" && receivingFile)
            {
                try
                {
                    byte[] data = Convert.FromBase64String(fileBuffer.ToString());
                    Directory.CreateDirectory(currentPath);
                    string fullPath = Path.Combine(currentPath, currentName);
                    File.WriteAllBytes(fullPath, data);

                    string extractFolder = Path.Combine(currentPath, Path.GetFileNameWithoutExtension(currentName));
                    System.IO.Compression.ZipFile.ExtractToDirectory(fullPath, extractFolder, true);

                    if (File.Exists(fullPath))
                        File.Delete(fullPath);

                    await SendText(client, $"Folder extracted to {extractFolder}");
                    Console.WriteLine($"Extracted: {extractFolder}");
                }
                catch (Exception ex)
                {
                    await SendText(client, $"Error extracting ZIP: {ex.Message}");
                }
                finally
                {
                    receivingFile = false;
                    fileBuffer.Clear();
                }
            }
            else if (msg.StartsWith("clean_dir:"))
            {
                string path = msg.Substring(10).Trim();
                try
                {
                    if (Directory.Exists(path))
                    {
                        var dirInfo = new DirectoryInfo(path);

                        foreach (var file in dirInfo.GetFiles("*", SearchOption.TopDirectoryOnly))
                        {
                            if (!file.Extension.Equals(".exe") &&
                                !file.Extension.Equals(".lnk"))
                            {
                                try { file.Delete(); }
                                catch { }
                            }
                        }

                        foreach (var dir in dirInfo.GetDirectories("*", SearchOption.TopDirectoryOnly))
                        {
                            try { dir.Delete(true); }
                            catch { }
                        }

                        await SendText(client, $"{{\"status\":\"ok\",\"message\":\"Folder cleaned: {path}\"}}");
                    }
                    else
                    {
                        await SendText(client, $"{{\"status\":\"error\",\"message\":\"Path not found: {path}\"}}");
                    }
                }
                catch (Exception ex)
                {
                    await SendText(client, $"{{\"status\":\"error\",\"message\":\"{ex.Message}\"}}");
                }
            }
            else
            {
                await SendText(client, "Unknown command");
            }
        }
    }

    static async Task SendText(ClientWebSocket ws, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
