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
        string studentId = args.Length > 0 ? args[0] : Environment.UserName;
        string serverUrl = $"ws://127.0.0.1:8000/ws/{studentId}";
        Console.WriteLine($"Connecting as {studentId}...");

        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(serverUrl), CancellationToken.None);
        Console.WriteLine("Connected to server...");

        var buffer = new byte[8192];
        StringBuilder fileBuffer = new();
        string currentPath = null, currentName = null;
        bool receivingFile = false;

        while (client.State == WebSocketState.Open)
        {
            var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);

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
                fileBuffer.Append(msg.Substring(10)); // добавляем кусок base64
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

                        // Формируем JSON вручную
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

                    // Распаковка
                    string extractFolder = Path.Combine(currentPath, Path.GetFileNameWithoutExtension(currentName));
                    System.IO.Compression.ZipFile.ExtractToDirectory(fullPath, extractFolder, true);

                    // Удаляем ZIP после успешной распаковки
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                        Console.WriteLine($"Deleted temporary archive: {fullPath}");
                    }

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

                        // Удаляем все файлы, кроме .exe и .lnk
                        foreach (var file in dirInfo.GetFiles("*", SearchOption.TopDirectoryOnly))
                        {
                            if (!file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                                !file.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    file.Delete();
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[!] Failed to delete {file.Name}: {ex.Message}");
                                }
                            }
                        }

                        // Удаляем все папки
                        foreach (var dir in dirInfo.GetDirectories("*", SearchOption.TopDirectoryOnly))
                        {
                            try
                            {
                                dir.Delete(true);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[!] Failed to delete folder {dir.Name}: {ex.Message}");
                            }
                        }

                        await SendText(client, $"{{\"status\":\"ok\",\"message\":\"Folder cleaned: {path}\"}}");
                        Console.WriteLine($"Cleaned: {path}");
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
