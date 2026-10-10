"""Run: python tests/EmbeddedDebugger.App.SmokeTests/loopback_ssh.py

Requires paramiko and Git Bash (or bash on Linux). Uses loopback and disposable
fixtures only; no saved profiles or real SSH credentials are read.
"""
import os
import pathlib
import shutil
import socket
import subprocess
import tempfile
import threading
import time

import paramiko

project = pathlib.Path(__file__).parent
git = shutil.which("git")
bash = shutil.which("bash") or (str(pathlib.Path(git).parent.parent / "bin/bash.exe") if git else None)
assert bash, "bash is required to execute the real performance command"
key = paramiko.RSAKey.generate(2048)
transports = []
fixture_root = tempfile.TemporaryDirectory(prefix="embedded-debugger-proc-")
proc = pathlib.Path(fixture_root.name) / "proc"
(proc / "net").mkdir(parents=True)


class Server(paramiko.ServerInterface):
    def __init__(self):
        self.size = (0, 0)
        self.window_size = (0, 0)
        self.samples = 0

    def check_auth_none(self, username):
        # OpenSSH sends its banner during user authentication, after service accept.
        banner = paramiko.Message()
        banner.add_byte(paramiko.common.cMSG_USERAUTH_BANNER)
        banner.add_string("AUTH-BANNER\n")
        banner.add_string("en")
        self.transport._send_message(banner)
        return paramiko.AUTH_FAILED

    def check_auth_password(self, username, password):
        time.sleep(1)
        return paramiko.AUTH_SUCCESSFUL if (username, password) == ("regression", "loopback-only") else paramiko.AUTH_FAILED

    def get_allowed_auths(self, username):
        return "password"

    def check_channel_request(self, kind, chanid):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def check_channel_pty_request(self, channel, term, width, height, pixelwidth, pixelheight, modes):
        assert term == b"xterm-256color"
        self.size = (width, height)
        self.window_size = self.size
        return True

    def check_channel_window_change_request(self, channel, width, height, pixelwidth, pixelheight):
        self.window_size = (width, height)
        return True

    def check_channel_shell_request(self, channel):
        def output():
            time.sleep(.05)
            channel.sendall("".join(f"MOTD-{i:03}: welcome 欢迎\r\n" for i in range(600)).encode())
            channel.sendall(f"PTY={self.size[0]}x{self.size[1]}\r\n".encode())
            for byte in "拆包中文".encode():
                channel.sendall(bytes([byte]))
                time.sleep(.04)
            channel.sendall(b"\r\nMOTD-END\r\nuser@test:~$ ")
            pending = b""
            while True:
                data = channel.recv(1024)
                if not data:
                    return
                pending += data
                while b"\r" in pending:
                    command, pending = pending.split(b"\r", 1)
                    if command.startswith(b"__tty_size__"):
                        marker = command[len(b"__tty_size__"):].decode()
                        channel.sendall(f"\r\nTTY_SIZE_{marker}={self.window_size[0]},{self.window_size[1]}\r\n".encode())
        threading.Thread(target=output, daemon=True).start()
        return True

    def check_channel_exec_request(self, channel, command):
        def output():
            time.sleep(.03)
            if command == b"cat /etc/os-release":
                stdout, stderr, status = b"ID=debian\n", b"", 0
            elif b"/proc/stat" in command:
                sample = self.samples
                self.samples += 1
                if sample == 2:
                    stdout, stderr, status = b"", b"temporary metrics failure", 1
                else:
                    (proc / "stat").write_text(f"cpu {400+sample*40} 0 0 {600+sample*60} 0 0 0 0 100 20\n" + "".join(f"cpu{i} 0 0 0 0\n" for i in range(4)))
                    mem = "MemAvailable: 400 kB\n" if sample < 3 else "MemFree: 100 kB\nBuffers: 50 kB\nCached: 200 kB\nSReclaimable: 60 kB\nShmem: 10 kB\n"
                    (proc / "meminfo").write_text("MemTotal: 1000 kB\n" + mem)
                    (proc / "loadavg").write_text("1.20 0.50 0.25 1/80 1000\n")
                    (proc / "uptime").write_text("90061.25 10000.5\n")
                    (proc / "net/dev").write_text("Inter-| Receive | Transmit\n face |bytes packets errs drop fifo frame compressed multicast|bytes packets errs drop fifo colls carrier compressed\n lo: 9999 0 0 0 0 0 0 0 9999 0 0 0 0 0 0 0\n eth0: 1000 0 0 0 0 0 0 0 500 0 0 0 0 0 0 0\n wlan0: 300 0 0 0 0 0 0 0 400 0 0 0 0 0 0 0\n")
                    # Preserve the exact shell/awk quoting, replacing only input paths.
                    script = command.decode().replace("/proc/", proc.as_posix() + "/")
                    result = subprocess.run([bash, "-c", script], capture_output=True, timeout=4)
                    stdout, stderr, status = result.stdout, result.stderr, result.returncode
            else:
                stdout, stderr, status = b"", b"unsupported test command", 1
            if stdout:
                channel.sendall(stdout)
            if stderr:
                channel.sendall_stderr(stderr)
            channel.send_exit_status(status)
            channel.close()
        threading.Thread(target=output, daemon=True).start()
        return True

    def check_channel_subsystem_request(self, channel, name):
        time.sleep(2)
        return False


listener = socket.socket()
listener.bind(("127.0.0.1", 0))
listener.listen()


def serve(client):
    transport = paramiko.Transport(client)
    transports.append(transport)
    transport.add_server_key(key)
    server = Server()
    server.transport = transport
    transport.start_server(server=server)
    channels = []
    while transport.is_active():
        channel = transport.accept(.2)
        if channel is not None:
            channels.append(channel)


def accept():
    while True:
        try:
            client, _ = listener.accept()
        except OSError:
            return
        threading.Thread(target=serve, args=(client,), daemon=True).start()


threading.Thread(target=accept, daemon=True).start()
try:
    env = dict(os.environ, EDBG_SSH_PORT=str(listener.getsockname()[1]))
    # An isolated published verifier also exercises compressed dependency loading.
    verifier = os.environ.get("EDBG_SSH_TEST_EXE")
    command = [verifier, "--ssh-protocol-check"] if verifier else ["dotnet", "run", "--project", str(project), "-c", "Debug", "-p:RunAnalyzers=false"]
    result = subprocess.run(command, env=env, timeout=120)
    raise SystemExit(result.returncode)
finally:
    listener.close()
    for transport in transports:
        transport.close()
    fixture_root.cleanup()
