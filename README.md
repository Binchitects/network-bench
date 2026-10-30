# netbench – in-memory HTTP(S) / FTP(S) / SMB test servers and a zero-copy client

Fast file servers for pushing high-bandwidth traffic at your network and services, plus a client that tells you **who is slow**: the client, the server, or the network. Everything is served from RAM and nothing touches disk.

| Component | What | How data moves |
|---|---|---|
| **memserve** (C#/.NET 10, `src/MemServe`) | HTTP/1.1, HTTPS, FTP, explicit FTPS | A random pattern (default 1 GiB) in a `memfd`. Files of **any size** (`1T.bin`, `37G`, …) are virtual and cycle through the pattern, so they cost no RAM. Plain TCP downloads use `sendfile()` and uploads `recv(MSG_TRUNC)`, so there's no copy into user space. TLS goes through OpenSSL with **kernel TLS** (kTLS) when the host has the `tls` module loaded: the kernel encrypts while `sendfile()`-ing the pattern. |
| **samba** (Samba 4.21, `docker/samba`) | SMB 2.0.2 – 3.1.1 server, optional SMB3 encryption | Real files on a `tmpfs` (RAM), created at start-up. `sendfile` for reads, `recvfile` for unsigned writes, async I/O, SMB3 multichannel. |
| **membench** (C#/.NET 10, `src/MemBench`) | test client: HTTP(S), FTP(S), files | Downloads with `recv(MSG_TRUNC)` (the kernel drops the data, nothing is copied), uploads with `sendfile()` from RAM. Over TLS it decrypts, or with `--tls-rx discard` drops the ciphertext unread so only the server is measured. Measures CPU per thread on **both** ends and prints a bottleneck verdict. |

The servers use **host networking**, so Docker's NAT and userland proxy are not in the data path. A Docker bridge costs about 5× in CPU per byte on this host; membench detects that too.

## Quick start

```bash
cp .env.example .env        # optional: ports, sizes, credentials
docker compose up -d --build
docker compose logs -f      # memserve prints a live Gb/s line every second while traffic flows
```

Defaults: HTTP `:80`, HTTPS `:443`, FTP and explicit FTPS `:21` (passive `50000-50100`), SMB `:445`, share `\\<host>\bench`, user `bench` / password `bench`. Without a certificate of your own, memserve makes a self-signed one at start (clients need `-k`).

## Run all tests: `membench SERVER`

One command, no options. membench finds memserve on the server (its HTTP, HTTPS and FTP ports, TLS, and the Samba share), runs the built-in test plan, checks every result and writes a report:

```bash
scripts/bench.sh 10.0.0.5            # via the clients image, report in ./membench-results/
dist/linux-x64/membench 10.0.0.5     # or the standalone binary (Windows: membench.exe 10.0.0.5)
```

The plan: **9 scenarios, 33 test cases, about 2 minutes** (each case: 1 s warmup + 3 s measured, in its own membench process).

| Scenario | Test cases |
|---|---|
| Single connection | HTTP, HTTPS, FTP, FTPS, each download and upload, 1 connection |
| Scaling: HTTP / HTTPS, download / upload | 4, 16 and 64 connections each |
| TLS cost | server-only TLS (ciphertext dropped unread); AES-128, AES-256, ChaCha20 with client decryption; client user-space TLS vs kernel TLS (8 connections) |
| FTP sessions | FTP download, FTP upload, FTPS download, 8 sessions each |
| Many connections | HTTP 256 connections, HTTPS 128 connections |
| SMB share | read with 1 and 8 threads, write with 4 threads, through the OS client |

Each case gets a status:

- **PASS**: it ran and nothing looked wrong.
- **WARN**: it ran but something needs a look: connections that got under 10% of the average rate, a server encrypting in user space (no kernel TLS), adding connections that *lowers* throughput by more than 20%, a link that negotiated under 1 Gb/s, or under 0.5 Gb/s on a real link.
- **FAIL**: errors, no data, or a timeout. membench exits with code 1, so it works in CI.
- **SKIP**: the protocol isn't available. For SMB on Linux, mount the share first (`sudo mount -t cifs //SERVER/bench /mnt/smb -o username=bench,password=bench`); on Windows, run `net use \\SERVER\bench /user:bench bench` first.

The report folder `membench-results/<time>-<server>/` holds `report.html` (open it in a browser), `report.md`, `results.csv` and `results.json`. The JSON has every case's full measurement and the exact command to re-run it on its own.

memserve advertises its ports and the Samba share in `/stats` (compose sets the share name). If memserve doesn't listen on 80, 8080, 8088, 443 or 8443, pass any of its ports: `membench 10.0.0.5:9000`.

## membench: find the bottleneck

```bash
docker build -t memserve-clients -f docker/clients/Dockerfile .        # membench + curl, aria2, lftp, smbclient, iperf3
docker run --rm --network host memserve-clients membench http://SERVER/ -c 4
```

Or as a standalone executable (no .NET or Docker needed on the client), for Windows and Linux:

```bash
scripts/publish-membench.sh          # -> dist/win-x64/membench.exe, dist/win-arm64/, dist/linux-x64/membench, dist/linux-arm64/
```

```bat
membench.exe http://SERVER/ -c 8
membench.exe https://SERVER/ -k -c 8
membench.exe Z:\10G.bin -c 8            &:: SMB: read a file on a share mapped with "net use Z: \\SERVER\bench"
```

**On Windows** everything works, with these differences: data is copied (there's no `MSG_TRUNC` or `sendfile`), TLS is SChannel (`SslStream`; `--cipher` only forces TLS 1.3), file mode uses unbuffered I/O (`FILE_FLAG_NO_BUFFERING`), and per-thread CPU comes from the Windows thread times. A Windows client is therefore CPU-bound earlier than a Linux one, and membench says so in its verdict. `MEMBENCH_PORTABLE=1` makes the Linux build use the same portable code paths.

Memory: about 45 MB at 1 connection and 55–90 MB at 64. Each connection gets a 256 KB receive buffer (copy mode and TLS; `--buffer`), and uploads come from a 16 MB pattern (`--backing`).

```
membench -> http://127.0.0.1:8088/1T  download, 4 connections, 3s + 1s warmup, rx=trunc
  link: same host (kernel loopback, no NIC involved)
  server: memserve stats at http://127.0.0.1:8088/stats (24 cpus)
  [   1.0s]  296.90 Gb/s   client  2.61 cores, top thread  66%   server  296.19 Gb/s,  4.01 cores, top thread 100%   (warmup)
  [   2.0s]  306.77 Gb/s   client  2.71 cores, top thread  69%   server  306.58 Gb/s,  4.00 cores, top thread 100%
  ...
Result: 298.20 Gb/s (34.71 GiB/s) over 3.0s, 104.15 GiB moved
  per connection: min 73.39 Gb/s  avg 74.55 Gb/s  max 76.38 Gb/s
  client: 2.72 cores, busiest thread 68% of a core
  server: 4.00 cores, busiest thread 100% of a core, server counted 298.17 Gb/s
  tcp: rtt 0.021 ms

Verdict: LOOPBACK SENDER-BOUND - the sending thread (server) uses 100% of a core; on loopback it also runs the receiver's TCP processing, so this is this host's per-connection ceiling, not a weakness of either side
  next: measure across a real NIC to compare client and server
```

Across a real network, the same run ends in `LINK-BOUND`, `CLIENT-BOUND`, `SERVER-BOUND`, `TCP-WINDOW-BOUND` or `NETWORK-BOUND` instead.

How it decides: it reads the local NIC's link speed, the busiest client thread (from `/proc`), the busiest server thread (from memserve's `/stats`, which now includes per-thread CPU), and TCP RTT and window limits. The verdicts, checked in this order:

| Verdict | Meaning | What to do |
|---|---|---|
| `LINK-BOUND` | ≥ 90% of the local NIC's speed | That's the wire. If it's below 1 Gb/s, check the cable and switch port |
| `LOOPBACK SENDER-BOUND` | same host, plain TCP: the sending thread is saturated, and on loopback it also runs the receiver's TCP stack | The per-connection ceiling of this host. Test across a real NIC |
| `CLIENT- AND SERVER-BOUND` | a thread is saturated on both ends | add connections (`-c`) |
| `CLIENT-BOUND` | a client thread is at ≥ 85% of a core | add connections; use `--rx trunc` (the default) |
| `SERVER-BOUND` | a server thread is at ≥ 85% of a core | add connections, or the server needs more CPU |
| `HOST-CPU-BOUND` | same host: client and server together use ≥ 85% of all CPUs | The machine is full. Put client and server on separate hosts |
| `CLIENT-BOUND` / `SERVER-BOUND` (whole machine) | one side uses ≥ 85% of all its CPUs, even if no single thread is pegged | That machine is out of CPU (with TLS: crypto) |
| `TCP-WINDOW-BOUND` | bandwidth × RTT is near the TCP buffer maximum | raise `tcp_rmem`/`tcp_wmem` and `rmem_max`/`wmem_max`, or add connections |
| `LATENCY-BOUND` (file mode) | no CPU saturated; each thread waits for one block | raise `-c` or `--block` |
| `NETWORK-BOUND` | neither side saturated, link not full | the path: switches, congestion, loss, or a slower NIC at the far end |
| `NOT THE CLIENT` | the client is idle and the server has no `/stats` | the server or network is slow; check the server's CPU |

Targets and useful options (`membench --help` lists everything):

```bash
membench http://SERVER/ -c 8                 # download (GET /1T, keep-alive, repeated)
membench http://SERVER/ -u -c 8              # upload (endless PUT, discarded by memserve)
membench ftp://SERVER/ -c 4 --stats http://SERVER:80/stats
membench ftp://user:pass@other-ftp/file.iso  # any FTP server (no server CPU then)
membench http://SERVER/ --rx copy            # copy into a buffer like a real application does
membench https://SERVER/ -k -c 8             # HTTPS: the client decrypts, like a real client
membench https://SERVER/ -k --tls-rx discard # HTTPS: drop ciphertext unread, measures the server's TLS alone
membench https://SERVER/ -k --cipher aes128  # force a TLS 1.3 suite: aes128 | aes256 | chacha20
membench ftpes://SERVER/ -k -c 4             # explicit FTPS (AUTH TLS, PROT P); -u for STOR
membench /mnt/smb/10G.bin -c 8               # file read on a cifs mount (O_DIRECT, no page cache)
membench /mnt/smb/test.bin -u -c 4           # file write (deleted afterwards unless --keep)
membench http://SERVER/ --bind 10.1.0.2      # pick the source NIC
membench http://SERVER/ --json               # machine-readable result
```

SMB at full speed: mount with the kernel client, which is much faster than smbclient, then point membench's file mode at it:

```bash
sudo mount -t cifs //SERVER/bench /mnt/smb -o username=bench,password=bench,multichannel,max_channels=4
membench /mnt/smb/10G.bin -c 8
```

## Benchmark script

`scripts/bench.sh <server>` runs the full test plan (`membench SERVER`, above) from the clients image and saves the report in `./membench-results/`. For quick manual checks with other tools (curl, aria2c, lftp, smbclient, iperf3), use the same image: `docker run --rm -it --network host memserve-clients`.

## Measured on the build machine

i7-13700K (24 threads), DDR5, loopback only. These are the **software ceilings**; a real network test needs a second machine.

| Test | curl / smbclient | membench |
|---|---|---|
| HTTP download, 1 connection | 66 Gb/s (client-bound: curl copies the data) | 114 Gb/s |
| HTTP download, 16 connections | ~410 Gb/s | ~1 Tb/s |
| HTTP upload, 1 / 8 connections | 12–34 Gb/s | 101 / 479 Gb/s |
| FTP RETR, 1 / 8 sessions | ~50 Gb/s | 90 / 480 Gb/s |
| SMB get, 1 / 8 connections | ~23 / ~80 Gb/s (smbclient) | — (needs a cifs mount) |
| HTTP / FTP, 32 – 512 connections, either direction | — | ~1 Tb/s total, no starved connections |
| SMB get, 16 – 128 connections | ~80 Gb/s total, 0 failures | — |

Above ~32 connections every number is flat: client and server share the 24 hardware threads, so this is the box's ceiling. SMB tops out lower because Samba costs about 1 core per 6 Gb/s; at 32 connections `smbd` used 13 cores and smbclient 7.

memserve lowers the priority (nice +10) of threads that move bulk data. When hundreds of transfers saturate the CPU, new connections and `/stats` still get served right away. Without this, connections that arrived late waited seconds for their thread to start: at 512 connections, a third of them got no data at all.

So the software ceiling is far above any single NIC: one connection fills a 100 GbE link, and a few connections fill 400 GbE, as long as the client is efficient. Ordinary clients (curl, browsers, `cp`) copy every byte and will be the bottleneck first.

## TLS: HTTPS, FTPS, SMB3 encryption

- **HTTPS** listens on `HTTPS_PORT` and serves the same paths as HTTP (`/<size>`, uploads, `/stats`). It supports TLS 1.2 and 1.3 and ALPN `http/1.1`. With OpenSSL 3.5 the default is TLS 1.3, AES-256-GCM, and the X25519MLKEM768 key exchange.
- **FTPS** is explicit (RFC 4217): `AUTH TLS`, `PBSZ 0`, `PROT P` on the normal FTP port. Data connections are encrypted after `PROT P`, and `PROT C` switches back to clear. Implicit FTPS (port 990) is not offered.
- **Certificate**: self-signed ECDSA P-256 by default (CN `memserve`, SANs localhost, 127.0.0.1 and the hostname). To use your own, mount PEM files and set `TLS_CERT`/`TLS_KEY` (see `compose.yaml`).
- **SMB3 encryption**: `SMB_ENCRYPT=default` lets clients ask for it (`smbclient --client-protection=encrypt`, `mount -o seal`). `required` forces it on every session (guests are refused). Sessions use AES-128-GCM.

```bash
curl -k -o /dev/null https://HOST/10G
curl -k --ssl-reqd -o /dev/null ftp://HOST/10G.bin          # explicit FTPS
lftp -e "set ftp:ssl-force true; set ssl:verify-certificate no; get 10G.bin -o /dev/null; quit" ftp://u:p@HOST
```

Measured on the build machine (loopback, membench, client and server on the same 24-thread CPU):

| Test | 1 connection | 8 connections | 16+ connections |
|---|---|---|---|
| plain HTTP download (for comparison) | 88 Gb/s | 480 Gb/s | ~1 Tb/s |
| HTTPS download, client decrypts | 22 Gb/s | 78 Gb/s | ~95 Gb/s (host CPU full) |
| HTTPS download, `--tls-rx discard` (server only) | 24 Gb/s | 124 Gb/s | ~135 Gb/s |
| HTTPS upload | 22 Gb/s | — | ~92 Gb/s (host CPU full) |
| FTPS RETR / STOR | 23 / 25 Gb/s | 79 / 78 Gb/s | ~87 Gb/s |
| SMB get, unencrypted / encrypted (smbclient) | 24 / 16 Gb/s | 79 / 44 Gb/s | — / 46 Gb/s |

The table above is user-space TLS (.NET `SslStream`). It costs about one core per 15–25 Gb/s on each end. The cipher matters less than you might expect: AES-128 is only about 5% faster than AES-256, because most of the cost is per-record copying and syscalls, not the cipher. ChaCha20 is about 40% slower on this AES-NI CPU.

### Kernel TLS (kTLS)

memserve's default TLS engine (`KTLS=auto`) drives OpenSSL directly. After the handshake, OpenSSL hands the session keys to the kernel, and from then on the kernel encrypts while `sendfile()` sends the in-RAM pattern (and decrypts uploads). There's no user-space copy and no syscall per 16 KB record. It needs the kernel's `tls` module, **which only root can load**:

```bash
sudo modprobe tls                                        # until the next reboot
echo tls | sudo tee /etc/modules-load.d/tls.conf         # load it at every boot
```

Nothing else needs root: the container uses kTLS with its default privileges once the module is loaded. Without the module, memserve logs `kernel tls module not loaded` and OpenSSL encrypts in user space. `KTLS=off` switches back to .NET `SslStream`, and `KTLS=zerocopy` adds OpenSSL's TX-zerocopy sendfile flag (no measurable gain with software kTLS; it matters for NICs with TLS offload). `/stats` counts how TLS connections were served (`"tls":{"ktls_tx":…,"ktls_rx":…,"userspace":…}`), and membench prints `server tls: kernel (kTLS)` or `user space`.

Server-only HTTPS download (`--tls-rx discard`), TLS 1.3 AES-256-GCM, memserve pinned to the P-cores, client on the E-cores:

| Connections | SslStream | kTLS | Gain |
|---|---|---|---|
| 1 | 24.7 Gb/s | 30.5 Gb/s | +23% |
| 4 | 84 Gb/s | 116 Gb/s | +38% |
| 8 | 115 Gb/s | 193 Gb/s | +67% |
| 16 | 111 Gb/s | 274 Gb/s | +146% |

membench uses kernel TLS on its side too (Linux, `--ktls auto`, the default). Downloads are decrypted by the kernel straight into membench's buffer, several records per `recvmsg()` call. Uploads are `sendfile()`d from RAM and encrypted by the kernel. Realistic HTTPS, where the client really decrypts, with the server on CPUs 0–7 and the client on 8–15:

| Test | client SslStream | client kTLS | Gain |
|---|---|---|---|
| download, 1 connection | 23 Gb/s | 30 Gb/s | +31% |
| download, 8 connections | 92 Gb/s | 140 Gb/s | +52% |
| FTPS RETR, 4 sessions | 64 Gb/s | 94 Gb/s | +46% |
| upload, 4 connections | 76 Gb/s | 95 Gb/s | +26% |

With client kTLS, the verdict moves from `CLIENT-BOUND` to `SERVER-BOUND`, so membench is no longer the limit for HTTPS. `--ktls off` uses `SslStream` instead; `--tls-rx discard` always does.

### Hybrid CPUs (Intel P/E cores)

On an i7-13700K, a TLS connection runs **about 3× faster on a P-core than on an E-core** (kTLS, 1 connection: 39 vs 12–17 Gb/s). Unpinned runs vary by 20% or more depending on where the scheduler puts each thread. memserve and membench detect hybrid CPUs and print a note. For repeatable numbers, pin the server to the P-cores (`MEMSERVE_CPUSET=0-15` in `.env`; check `lscpu -e` for your CPU) and run the client with `taskset -c …` or `docker run --cpuset-cpus …`. Plain TCP is far less sensitive, and there the E-cores add throughput, so leave it unpinned for many-connection plain tests.

## Configuration (`.env`)

| Variable | Default | Meaning |
|---|---|---|
| `HTTP_PORT` / `HTTPS_PORT` / `FTP_PORT` | 80 / 443 / 21 | memserve ports (`0` disables one) |
| `FTP_TLS` | true | allow explicit FTPS (`AUTH TLS`) on `FTP_PORT` |
| `TLS_CERT` / `TLS_KEY` | – | PEM certificate and key paths inside the container; empty = self-signed |
| `KTLS` | auto | TLS engine: `auto` (OpenSSL, kernel TLS when the `tls` module is loaded), `zerocopy`, `off` (.NET SslStream) |
| `MEMSERVE_CPUSET` | – | CPUs memserve may use, e.g. `0-15` for the P-cores of a hybrid CPU; empty = all |
| `FTP_PASV_PORTS` | 50000-50100 | passive data ports |
| `FTP_PASV_ADDR` | – | IP announced in `PASV` replies; only needed behind NAT or bridge mode |
| `MEMSERVE_BACKING` | 1G | RAM for the random pattern. Bigger means less repetition inside one file; it doesn't change speed |
| `MEMSERVE_FILES` | 1M … 1T | virtual files shown in listings (any other size still works on request) |
| `SMB_FILES` | 1G,10G | real files created on the tmpfs; **they use this much RAM** |
| `SMB_TMPFS_SIZE` | 16g | tmpfs cap. It must cover `SMB_FILES` plus uploads, which stay in RAM until deleted. RAM is only used as data is written |
| `SMB_USER` / `SMB_PASS` / `SMB_SHARE` | bench / bench / bench | credentials and share name |
| `SMB_GUEST` | yes | allow guest access (unknown users map to guest) |
| `SMB_ENCRYPT` | default | SMB3 encryption: `default` (client decides), `off`, `desired`, `required` (also disables guests) |
| `SMB_INTERFACES` | all | interfaces Samba advertises for multichannel, e.g. `eth0 eth1` |
| `SMB_PORT` | 445 | SMB port |

Without host networking (some Docker Desktop setups), use the bridge override. Expect lower numbers:

```bash
docker compose -f compose.yaml -f compose.bridge.yaml up -d
```

## Using the servers with ordinary tools

**HTTP**: `GET /<size>` (`/10G`, `/512M`, `/1.5T.bin`, with ranges) returns random data. `PUT /name` or `POST /anything` is an upload sink that answers with JSON `{bytes, seconds, gbps}`. `GET /stats` returns counters and server CPU; `GET /health` returns `ok`.

```bash
curl -o /dev/null http://HOST/10G
aria2c -x16 -s16 -d /dev -o null --allow-overwrite=true http://HOST/100G
```

**HTTPS**: the same paths over TLS, e.g. `curl -k -o /dev/null https://HOST/10G`.

**FTP**: any user name and password, one flat directory with the same virtual files. `STOR` is a sink, and `REST` (resume) works. Explicit FTPS works too: `curl -k --ssl-reqd ftp://HOST/10G.bin`.

**SMB**: `smbclient //HOST/bench -U bench%bench -c 'get 10G.bin /dev/null'`, or on Windows `net use Z: \\HOST\bench /user:bench bench`. Windows 11 24H2 and later require signing and refuse guest logons, so log in as `bench`.

## Running without Docker

```bash
cd src/MemServe && dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
./out/memserve --http-port 8080 --ftp-port 2121
scripts/publish-membench.sh linux-x64 && dist/linux-x64/membench http://SERVER:8080/ -c 4
```

memserve's zero-copy paths and kernel TLS are Linux-only; it has a plain-`send()` fallback elsewhere, but only Linux has been tested. membench runs on Linux and Windows (see above).

## Tuning for 25–400 Gb/s links

Run membench first: it tells you which of these matters.

- **TCP buffers** (`TCP-WINDOW-BOUND`): `sysctl -w net.core.rmem_max=268435456 net.core.wmem_max=268435456 net.ipv4.tcp_rmem="4096 131072 268435456" net.ipv4.tcp_wmem="4096 131072 268435456"` on both ends.
- **Connections** (`*-BOUND` on one thread): one TCP flow costs about one core at 50–100 Gb/s (TLS: 15–25 Gb/s). Use `-c 4` to `-c 16`, and SMB multichannel.
- **MTU 9000** end to end, if your network supports jumbo frames: fewer packets for the same bytes.
- **Separate machines**: on one host, client and server share CPU and memory bandwidth (`LOOPBACK SENDER-BOUND`).
- **TLS**: load the `tls` module on the server (`sudo modprobe tls`) so memserve uses kernel TLS (+50% to +150%), and pin to P-cores on hybrid CPUs.
- **SMB**: use the kernel cifs client with `multichannel,max_channels=4` (Windows does multichannel automatically). Signing costs about 15–25% and encryption about 35–45%. Samba's per-connection limit is also lower than memserve's.

## Limitations

- memserve uploads are discarded. A file uploaded with `PUT` or `STOR` shows up with its size, but reading it back returns pattern data.
- memserve uses one flat directory. FTPS is explicit only (no implicit FTPS on port 990), and data connections don't require TLS session reuse.
- SMB files are real tmpfs files, so their size is real RAM.
- membench speaks HTTP/1.1 and HTTPS (Content-Length or read-until-close), FTP and explicit FTPS in passive mode, and files. It has no native SMB client: use a cifs mount on Linux or a mapped drive on Windows, plus file mode.
- The Windows build of membench was cross-compiled and its portable code paths were tested on Linux (`MEMBENCH_PORTABLE=1`). It hasn't been run on a real Windows machine yet; the Windows-only calls (thread IDs, `SIO_TCP_INFO`, unbuffered files) are untested.
- When membench stops mid-download it aborts its connections, and memserve stops sending once it sees the client hang up. memserve treats a client that half-closes during a bulk kTLS download as gone.
