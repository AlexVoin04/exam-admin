using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class StudentAgent
{
    static async Task Main()
    {
        var client = new ClientWebSocket();
        string studentId = "student01";
        string serverUrl = $"ws://127.0.0.1:8000/ws/{studentId}";
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
