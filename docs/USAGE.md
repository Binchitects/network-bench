# netbench — simple usage guide

netbench is two programs plus a Samba container:

| Piece | What it does |
|---|---|
| **memserve** | The server. Serves endless random data over HTTP, HTTPS, FTP and FTPS from RAM. |
| **membench** | The client. Measures throughput and says whether the client, the server or the network is the limit. |
| **samba** | Optional SMB 2/3 share for Windows/SMB tests. |

This guide is the short version. Full details, tuning and measurements are in the [README](../README.md).

---

## 1. Get the builds

GitHub Actions builds everything on every push to `main`, on pull requests and on manual runs
(**Actions → build → Run workflow**; if the tab is missing, enable Actions in **Settings → Actions → General**).
Open a run and download what you need from **Artifacts**:

| Artifact | Contents |
|---|---|
| `membench-linux-x64`, `membench-linux-arm64` | standalone client, no .NET install needed |
| `membench-win-x64`, `membench-win-arm64` | `membench.exe` for Windows |
| `memserve-linux-x64`, `memserve-linux-arm64` | standalone server |

Tag a commit `v1.2.3` to get the same files zipped and attached to a GitHub **Release**:

```bash
git tag v1.2.3
git push origin v1.2.3
```

Docker images are published to GHCR on every non-PR build:

```
ghcr.io/xciscox/network-bench/memserve:latest
ghcr.io/xciscox/network-bench/samba:latest
ghcr.io/xciscox/network-bench/clients:latest   # membench + curl, aria2, lftp, smbclient, iperf3
```

(These are this repository. If you fork it, images go to `ghcr.io/<your-owner>/<your-repo>/<image>`.)

If a GHCR package is private, pull it with a classic PAT that has `read:packages`,
or make the package public under the repository's **Packages** settings:

```bash
echo "$CR_PAT" | docker login ghcr.io -u YOUR_GITHUB_USER --password-stdin
```

Artifacts are not executable after download, so run `chmod +x` on Linux/macOS (Windows ignores it):

```bash
unzip netbench-membench-linux-x64.zip -d netbench
chmod +x netbench/membench
```

### Or build locally

```bash
dotnet publish src/MemServe/MemServe.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
dotnet publish src/MemBench/MemBench.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
scripts/publish-membench.sh          # or: all four client platforms into dist/
```

---

## 2. Start the server

### Standalone (Linux)

```bash
sudo ./memserve                       # HTTP :80, HTTPS :443, FTP/FTPS :21
```

Without root, use high ports:

```bash
./memserve --http-port 8080 --https-port 8443 --ftp-port 2121
```

The server prints the addresses it listens on and a live Gb/s line while traffic flows.
Without your own certificate it creates a self-signed one, so clients need `-k`.

### Docker Compose (recommended)

```bash
cp .env.example .env                  # optional: ports, RAM sizes, SMB credentials
docker compose up -d --build
docker compose logs -f
```

Host networking is used, so there is no NAT in the data path.
On Docker Desktop / a host without host networking:

```bash
docker compose -f compose.yaml -f compose.bridge.yaml up -d
```

### Published images instead of a local build

```bash
docker run -d --name memserve --network host ghcr.io/xciscox/network-bench/memserve:latest
docker run -d --name samba --network host --tmpfs /data:size=16g,mode=1777 ghcr.io/xciscox/network-bench/samba:latest
```

Defaults: HTTP `:80`, HTTPS `:443`, FTP/FTPS `:21` (passive `50000-50100`),
SMB `:445`, share `\\<host>\bench`, user/password `bench` / `bench`.

---

## 3. Run the client

### Full test plan (one command)

```bash
./membench 10.0.0.5
```

It discovers the server's HTTP/HTTPS/FTP ports and the SMB share, runs 33 test cases
(about 2 minutes), then writes a report. Exit code is `1` if anything failed, so it works in CI.

A server on a different port:

```bash
./membench 10.0.0.5:8080
```

### One quick measurement

```bash
./membench http://10.0.0.5:8080/ -c 4              # download, 4 connections
./membench http://10.0.0.5:8080/ -u -c 4           # upload (data is discarded)
./membench https://10.0.0.5:8443/ -k -c 4          # HTTPS, ignore self-signed cert
./membench ftp://10.0.0.5:2121/ -c 4               # FTP download
./membench ftpes://10.0.0.5:2121/ -k -c 4          # explicit FTPS
./membench /mnt/smb/10G.bin -c 4                   # read a mounted SMB file
./membench http://10.0.0.5:8080/ --json            # machine-readable result
```

Output ends with a verdict such as `LINK-BOUND`, `CLIENT-BOUND`, `SERVER-BOUND` or
`NETWORK-BOUND`, plus a suggested next step.

### Windows

```bat
membench.exe 10.0.0.5
membench.exe http://10.0.0.5:8080/ -c 4
```

Windows copies data (no zero-copy syscalls), so it reaches its CPU limit earlier than Linux.

### Client container (nothing to install)

```bash
docker run --rm -it --network host ghcr.io/xciscox/network-bench/clients:latest
membench 10.0.0.5
```

---

## 4. Where the report goes

The full test plan writes:

```
membench-results/<time>-<server>/
├── report.html      # open in a browser
├── report.md
├── results.csv
└── results.json     # every measurement + re-run commands
```

Each test case is `PASS`, `WARN`, `SKIP` (protocol unavailable) or `FAIL`.

---

## 5. Common issues

| Symptom | Fix |
|---|---|
| `cannot bind port 80` | Run as root, use `--http-port 8080` and friends, or `sudo setcap cap_net_bind_service=+ep $(readlink -f $(command -v memserve))` |
| HTTPS/FTPES certificate error | Use `-k` (self-signed certificate) |
| Server found but SMB cases are `SKIP` | Mount the share first: `sudo mount -t cifs //SERVER/bench /mnt/smb -o username=bench,password=bench` (Windows: `net use \\SERVER\bench /user:bench bench`) |
| Client can't reach the server | Check the firewall for TCP 80/443/21/445 and UDP/TCP passive ports `50000-50100` |
| Docker data path looks slow | Use host networking (`network_mode: host`, Linux) rather than the bridge |
| `membench` says `no memserve found` | Pass `host:port` of the HTTP port, e.g. `10.0.0.5:8080` |

TLS is much faster with kernel TLS enabled on the server host:

```bash
sudo modprobe tls
echo tls | sudo tee /etc/modules-load.d/tls.conf
```
