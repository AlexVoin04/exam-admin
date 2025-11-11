from fastapi import FastAPI, WebSocket, UploadFile, File, Form
from fastapi.responses import JSONResponse
import asyncio
import base64
import os

app = FastAPI()
clients = {}  # { client_id: { "ws": websocket, "queue": asyncio.Queue() } }

@app.websocket("/ws/{client_id}")
async def websocket_endpoint(websocket: WebSocket, client_id: str):
    await websocket.accept()
    clients[client_id] = {"ws": websocket, "queue": asyncio.Queue()}
    print(f"[+] {client_id} connected")

    try:
        while True:
            data = await websocket.receive_text()
            print(f"<-- {client_id}: {data}")
            # Сохраняем последнее сообщение в очередь
            await clients[client_id]["queue"].put(data)
    except Exception as e:
        print(f"[-] {client_id} disconnected: {e}")
        clients.pop(client_id, None)

@app.post("/send/{client_id}")
async def send_command(client_id: str, command: str):
    client = clients.get(client_id)
    if not client:
        return JSONResponse({"status": "offline"})

    ws = client["ws"]
    queue = client["queue"]

    await ws.send_text(command)
    print(f"--> Sent to {client_id}: {command}")

    try:
        # ждём ответ из очереди, не блокируя основной приёмник
        result = await asyncio.wait_for(queue.get(), timeout=5.0)
        return JSONResponse({"status": "ok", "result": result})
    except asyncio.TimeoutError:
        return JSONResponse({"status": "timeout"})

@app.post("/upload/{client_id}")
async def upload_file(
    client_id: str,
    file: UploadFile = File(...),
    target_path: str = Form("C:\\ExamFiles")
):
    client = clients.get(client_id)
    if not client:
        return JSONResponse({"status": "offline"})

    ws = client["ws"]
    content = await file.read()
    encoded = base64.b64encode(content).decode("utf-8")

    # Используем безопасный разделитель
    sep = "|||"

    await ws.send_text(f"file_start{sep}{target_path}{sep}{file.filename}")

    chunk_size = 8000
    for i in range(0, len(encoded), chunk_size):
        chunk = encoded[i:i + chunk_size]
        await ws.send_text(f"file_data:{chunk}")

    await ws.send_text("file_end")

    print(f"--> Sent file '{file.filename}' to {client_id} → {target_path}")
    return JSONResponse({
        "status": "sent",
        "filename": file.filename,
        "target_path": target_path
    })