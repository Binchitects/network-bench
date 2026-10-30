#!/bin/sh
# Generates test files on the tmpfs at /data, writes smb.conf from env vars, runs smbd in the foreground.
set -eu
DATA=/data
: "${SMB_PORT:=445}" "${SMB_SHARE:=bench}" "${SMB_USER:=bench}" "${SMB_PASS:=bench}"
: "${SMB_GUEST:=yes}" "${SMB_FILES:=1G,10G}" "${SMB_LOG_LEVEL:=1}" "${SMB_INTERFACES:=}" "${SMB_NETBIOS_NAME:=MEMSMB}"
: "${SMB_ENCRYPT:=default}"   # SMB3 encryption: default (client decides) | off | desired | required

log() { echo "[samba-init] $*"; }

# "10G" / "512M" / "4096" -> bytes (binary units)
to_bytes() {
    n=$(echo "$1" | sed 's/[^0-9]//g'); u=$(echo "$1" | sed 's/[0-9]//g' | tr 'a-z' 'A-Z' | cut -c1)
    case "$u" in
        "") echo "$n" ;; K) echo $((n << 10)) ;; M) echo $((n << 20)) ;; G) echo $((n << 30)) ;; T) echo $((n << 40)) ;;
        *) log "bad size '$1'"; exit 1 ;;
    esac
}

fstype=$(stat -f -c %T "$DATA" 2>/dev/null || echo unknown)
[ "$fstype" = tmpfs ] || log "WARNING: $DATA is '$fstype', not tmpfs - files live on disk, not in RAM"

# --- test files: one random seed (<= 1 GiB) from urandom, replicated with cat (memcpy speed)
total=0
for f in $(echo "$SMB_FILES" | tr ',;' '  '); do total=$((total + $(to_bytes "$f"))); done
avail=$(( $(stat -f -c '%a * %S' "$DATA") ))
if [ "$total" -gt "$avail" ]; then
    log "ERROR: SMB_FILES need $((total >> 20)) MiB but $DATA has only $((avail >> 20)) MiB free - raise SMB_TMPFS_SIZE"
    exit 1
fi
if [ "$total" -gt 0 ]; then
    seed_size=$((1 << 30)); [ "$total" -lt "$seed_size" ] && seed_size=$total
    seed="$DATA/.seed"
    start=$(date +%s)
    head -c "$seed_size" /dev/urandom > "$seed"
    for f in $(echo "$SMB_FILES" | tr ',;' '  '); do
        size=$(to_bytes "$f"); name="$DATA/$(echo "$f" | tr 'a-z' 'A-Z').bin"
        if [ -f "$name" ] && [ "$(stat -c %s "$name")" = "$size" ]; then continue; fi
        full=$((size / seed_size)); rest=$((size % seed_size))
        { i=0; while [ $i -lt $full ]; do cat "$seed"; i=$((i + 1)); done; head -c "$rest" "$seed"; } > "$name"
        log "created $(basename "$name") ($size bytes)"
    done
    rm -f "$seed"
    log "test files ready in $(( $(date +%s) - start ))s"
fi

# --- account
id "$SMB_USER" >/dev/null 2>&1 || adduser -D -H -s /sbin/nologin "$SMB_USER"
chown -R "$SMB_USER" "$DATA"
printf '%s\n%s\n' "$SMB_PASS" "$SMB_PASS" | smbpasswd -s -a "$SMB_USER" >/dev/null

case "$SMB_ENCRYPT" in default|off|if_required|desired|required) ;; *) log "bad SMB_ENCRYPT '$SMB_ENCRYPT'"; exit 1 ;; esac
# encrypted sessions need an authenticated user; guests cannot be encrypted
[ "$SMB_ENCRYPT" = required ] && SMB_GUEST=no

case "$SMB_GUEST" in yes|true|1|on) guest=yes; map_guest="Bad User" ;; *) guest=no; map_guest=Never ;; esac

mkdir -p /etc/samba
cat > /etc/samba/smb.conf <<CONF
[global]
   server role = standalone server
   workgroup = WORKGROUP
   netbios name = $SMB_NETBIOS_NAME
   server string = memserve samba (tmpfs)
   disable netbios = yes
   smb ports = $SMB_PORT
   server min protocol = SMB2_02
   server multi channel support = yes
   map to guest = $map_guest
   guest account = nobody
   idmap config * : backend = tdb
   idmap config * : range = 3000-7999
   load printers = no
   printing = bsd
   printcap name = /dev/null
   disable spoolss = yes
   log level = $SMB_LOG_LEVEL
   # throughput: zero-copy reads (sendfile) and writes (recvfile/splice, used when the session is unsigned)
   use sendfile = yes
   min receivefile size = 16384
   aio read size = 1
   aio write size = 1
   strict sync = no
   sync always = no
   kernel oplocks = no
   # SMB3 encryption (AES-GCM/CCM); encrypted traffic cannot use sendfile/recvfile
   server smb encrypt = $SMB_ENCRYPT
$( [ -n "$SMB_INTERFACES" ] && printf '   interfaces = %s\n' "$SMB_INTERFACES" )

[$SMB_SHARE]
   path = $DATA
   read only = no
   browseable = yes
   guest ok = $guest
   force user = $SMB_USER
   create mask = 0664
   directory mask = 0775
   ea support = no
   store dos attributes = no
CONF
testparm -s >/dev/null 2>&1 || { testparm -s; exit 1; }

log "share \\\\<host>\\$SMB_SHARE on port $SMB_PORT  (user $SMB_USER, guest $guest, encryption $SMB_ENCRYPT)"
exec smbd --foreground --no-process-group --debug-stdout
