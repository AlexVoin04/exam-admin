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

        while (client.State == WebSocketState.Open)
        {
            var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);

            if (msg.StartsWith("file:"))
            {
                // Формат: file:<путь>:<имя_файла>:<base64>
                var parts = msg.Split(':', 4);
                string targetPath = parts[1];
                string filename = parts[2];
                string base64 = parts[3];

                try
                {
                    byte[] fileData = Convert.FromBase64String(base64);
                    Directory.CreateDirectory(targetPath);
                    string fullPath = Path.Combine(targetPath, filename);
                    File.WriteAllBytes(fullPath, fileData);

                    Console.WriteLine($"File saved to {fullPath}");
                    await SendText(client, $"File '{filename}' saved to {targetPath}");
                }
                catch (Exception ex)
                {
                    await SendText(client, $"Error saving file: {ex.Message}");
                }
            }
            else if (msg.StartsWith("listdir"))
            {
                string path = msg.Substring(8).Trim();
                string response;
                try
                {
                    if (Directory.Exists(path))
                        response = string.Join(", ", Directory.GetFiles(path));
                    else
                        response = "Directory not found";
                }
                catch (Exception ex)
                {
                    response = "Error: " + ex.Message;
                }
                await SendText(client, response);
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
