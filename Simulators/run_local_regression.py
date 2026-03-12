import os
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
OUTPUT_ROOT = REPO_ROOT / "Simulators" / "output" / f"regression-{time.strftime('%Y-%m-%dT%H-%M-%S')}"
DB_PATH = OUTPUT_ROOT / "OCPP.Core.e2e.sqlite"
SINK_DIR = OUTPUT_ROOT / "emails"

SERVER_HTTP_BASE = os.environ.get("SERVER_HTTP_BASE", "http://127.0.0.1:19081")
MGMT_HTTP_BASE = os.environ.get("MGMT_HTTP_BASE", "http://127.0.0.1:19082")
SERVER_API_BASE = f"{SERVER_HTTP_BASE}/API"

SERVER_DLL = REPO_ROOT / "OCPP.Core.Server" / "bin" / "Debug" / "net8.0" / "OCPP.Core.Server.dll"
MANAGEMENT_DLL = REPO_ROOT / "OCPP.Core.Management" / "bin" / "Debug" / "net8.0" / "OCPP.Core.Management.dll"


def wait_for_http(url: str, label: str, timeout_seconds: int = 90) -> None:
    started_at = time.time()
    while time.time() - started_at < timeout_seconds:
        try:
            with urllib.request.urlopen(url, timeout=1):
                return
        except urllib.error.HTTPError as error:
            if 300 <= error.code < 500:
                return
        except Exception:
            pass

        time.sleep(1)

    raise RuntimeError(f"Timed out waiting for {label} at {url}")


def start_process(name: str, command: list[str], extra_env: dict[str, str]) -> subprocess.Popen:
    env = os.environ.copy()
    env.update(
        {
            "ASPNETCORE_ENVIRONMENT": "Development",
            "ConnectionStrings__SqlServer": "",
            "ConnectionStrings__SQLite": f"Filename={DB_PATH};foreign keys=True",
        }
    )
    env.update(extra_env)
    return subprocess.Popen(command, cwd=REPO_ROOT, env=env)


def run_command(name: str, command: list[str], extra_env: dict[str, str] | None = None) -> None:
    env = os.environ.copy()
    if extra_env:
      env.update(extra_env)

    completed = subprocess.run(command, cwd=REPO_ROOT, env=env, check=False)
    if completed.returncode != 0:
        raise RuntimeError(f"{name} failed with exit code {completed.returncode}")


def terminate_process(process: subprocess.Popen) -> None:
    if process.poll() is not None:
        return

    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()


def main() -> int:
    if not SERVER_DLL.exists() or not MANAGEMENT_DLL.exists():
        raise RuntimeError("Build the solution first so the server and management DLLs exist in bin/Debug/net8.0.")

    OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
    SINK_DIR.mkdir(parents=True, exist_ok=True)

    server = start_process(
        "server",
        ["dotnet", str(SERVER_DLL)],
        {
            "Kestrel__Endpoints__Http__Url": SERVER_HTTP_BASE,
            "Notifications__SinkDirectory": str(SINK_DIR),
            "Hangfire__EnableDashboard": "false",
        },
    )

    management = start_process(
        "management",
        ["dotnet", str(MANAGEMENT_DLL)],
        {
            "Kestrel__Endpoints__Http__Url": MGMT_HTTP_BASE,
            "ServerApiUrl": SERVER_API_BASE,
            "Hangfire__EnableDashboard": "false",
        },
    )

    try:
        wait_for_http(f"{SERVER_HTTP_BASE}/", "server")
        wait_for_http(f"{MGMT_HTTP_BASE}/Public/Map", "management")

        run_command(
            "e2e-smoke",
            ["node", "Simulators/e2e_smoke_test.mjs"],
            {
                "SERVER_HTTP_BASE": SERVER_HTTP_BASE,
                "SERVER_API_BASE": SERVER_API_BASE,
                "SERVER_WS_BASE": SERVER_HTTP_BASE.replace("http", "ws", 1) + "/OCPP",
                "MGMT_HTTP_BASE": MGMT_HTTP_BASE,
            },
        )

        for protocol in ("1.6", "2.0.1", "2.1"):
            for scenario in ("stop_then_unplug", "suspended_idle_then_unplug", "quiet_hours_idle_excluded", "live_meter_progress"):
                run_command(
                    f"scenario-{protocol}-{scenario}",
                    ["node", "Simulators/ocpp_scenario_runner.mjs", f"--protocol={protocol}", f"--scenario={scenario}"],
                    {
                        "SERVER_HTTP_BASE": SERVER_HTTP_BASE,
                        "SERVER_API_BASE": SERVER_API_BASE,
                        "SERVER_WS_BASE": SERVER_HTTP_BASE.replace("http", "ws", 1) + "/OCPP",
                        "SQLITE_DB_PATH": str(DB_PATH),
                    },
                )

        run_command(
            "playwright",
            ["npm", "--prefix", "Simulators/playwright", "test"],
            {
                "MGMT_HTTP_BASE": MGMT_HTTP_BASE,
            },
        )
    finally:
        terminate_process(management)
        terminate_process(server)

    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
