import shutil
import tempfile
import zipfile
from fastapi import FastAPI, WebSocket, UploadFile, File, Form
from fastapi.responses import JSONResponse
import asyncio
import base64
import json
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
            # Сохранение последнего сообщения в очередь
            await clients[client_id]["queue"].put(data)
    except Exception as e:
        print(f"[-] {client_id} disconnected: {e}")
        clients.pop(client_id, None)

@app.get("/clients")
async def list_clients():
    return JSONResponse({
        "connected": list(clients.keys()),
        "count": len(clients)
    })

@app.post("/send/{client_id}")
async def send_command(client_id: str, command: str):
    client = clients.get(client_id)
    if not client:
        return JSONResponse({"status": "offline"})

    ws = client["ws"]
    queue = client["queue"]

    while not queue.empty():
        try:
            queue.get_nowait()
        except:
            break

    await ws.send_text(command)
    print(f"--> Sent to {client_id}: {command}")

    try:
        # ожидание ответа из очереди, не блокируя основной приёмник
        result = await asyncio.wait_for(queue.get(), timeout=10.0)
        try:
            parsed = json.loads(result)
            return {"status": "ok", "result": parsed}
        except json.JSONDecodeError:
            return {"status": "ok", "result": result}
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

    # безопасный разделитель
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

@app.post("/upload_folder/{client_id}")
async def upload_folder(client_id: str, source_path: str = Form(...), target_path: str = Form(...)):
    """
    Упаковывает папку source_path в zip и отправляет клиенту,
    который потом её распакует в target_path.
    """
    client = clients.get(client_id)
    if not client:
        return JSONResponse({"status": "offline"})

    ws = client["ws"]

    if not os.path.isdir(source_path):
        return JSONResponse({"status": "error", "message": "Source folder not found"})

    # Временный ZIP
    tmp_zip = tempfile.mktemp(suffix=".zip")
    shutil.make_archive(tmp_zip[:-4], "zip", source_path)

    # Читаем и кодируем ZIP
    with open(tmp_zip, "rb") as f:
        content = f.read()
    encoded = base64.b64encode(content).decode("utf-8")

    sep = "|||"
    zip_name = os.path.basename(source_path.rstrip("\\/")) + ".zip"

    await ws.send_text(f"zip_start{sep}{target_path}{sep}{zip_name}")

    chunk_size = 8000
    for i in range(0, len(encoded), chunk_size):
        await ws.send_text(f"zip_data:{encoded[i:i + chunk_size]}")

    await ws.send_text("zip_end")

    os.remove(tmp_zip)
    print(f"--> Sent folder '{source_path}' as {zip_name} → {target_path}")
    return {"status": "sent", "folder": source_path, "target": target_path}

@app.post("/clean_folder/{client_id}")
async def clean_folder(client_id: str, path: str = Form(...)):
    """
    Очищает указанную папку на клиенте, кроме .exe и .lnk файлов.
    """
    client = clients.get(client_id)
    if not client:
        return JSONResponse({"status": "offline"})

    ws = client["ws"]
    queue = client["queue"]

    command = f"clean_dir:{path}"
    await ws.send_text(command)
    print(f"--> Sent cleanup command to {client_id}: {path}")

    try:
        result = await asyncio.wait_for(queue.get(), timeout=10.0)
        return JSONResponse({"status": "ok", "result": json.loads(result)})
    except asyncio.TimeoutError:
        return JSONResponse({"status": "timeout"})
    except json.JSONDecodeError:
        return JSONResponse({"status": "ok", "result": result})

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)