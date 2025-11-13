@echo off
chcp 65001 >nul
set PYTHONUTF8=1

cd /d "%~dp0"

echo ================================================
echo [ FastAPI Server Setup ^& Run Utility ]
echo ================================================

:: ====== НАСТРОЙКИ ПО УМОЛЧАНИЮ ======
set UPDATE_PIP=0
:: Можно изменить при запуске:  setup_and_run.bat UPDATE_PIP=1
:: =============================

:: Если при запуске передан аргумент формата UPDATE_PIP=1 — применяем его
for %%A in (%*) do (
    for /f "tokens=1,2 delims==" %%B in ("%%A") do (
        if /I "%%B"=="UPDATE_PIP" set UPDATE_PIP=%%C
    )
)

echo [INFO] Параметры запуска:
echo     UPDATE_PIP = %UPDATE_PIP%
echo --------------------------------

:: Проверяем наличие Python
python --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Python не найден. Установите Python 3.11+ и добавьте в PATH.
    pause
    exit /b
)

:: Создаём виртуальное окружение, если его нет
if not exist "venv\" (
    echo [INFO] Создаю виртуальное окружение...
    python -m venv venv
)

:: Активируем окружение
call venv\Scripts\activate

:: Устанавливаем зависимости
if exist "requirements.txt" (
    echo [INFO] Устанавливаю зависимости из requirements.txt...
    if "%UPDATE_PIP%"=="1" (
        echo [INFO] Обновляю pip...
        python -m pip install --upgrade pip
    ) else (
        echo [INFO] Пропускаю обновление pip.
    )
    pip install -r requirements.txt
) else (
    echo [WARN] Файл requirements.txt не найден. Устанавливаю стандартные пакеты...
    if "%UPDATE_PIP%"=="1" (
        echo [INFO] Обновляю pip...
        python -m pip install --upgrade pip
    ) else (
        echo [INFO] Пропускаю обновление pip.
    )
    pip install fastapi uvicorn python-multipart
)

:: Запускаем сервер
echo [INFO] Запускаю FastAPI сервер...
echo --------------------------------
uvicorn main:app --host 0.0.0.0 --port 8000 --log-level info
echo --------------------------------
echo [INFO] Сервер завершил работу.
pause
