namespace MemServe.Native;

/// <summary>
/// Direct reads from a socket whose receive side is kernel TLS (after OpenSSL enabled kTLS RX). The kernel decrypts
/// straight into our buffer and returns as many application-data records as fit per call, where SSL_read on a kTLS
/// socket fetches one 16 KiB record per syscall. Non-data records arrive alone, tagged by a control message.
/// </summary>
internal static unsafe class Ktls
{
    private const int SOL_TLS = 282, TLS_GET_RECORD_TYPE = 2;
    private const byte RecordAlert = 21, RecordHandshake = 22, RecordApplicationData = 23;

    // struct msghdr / iovec / cmsghdr on LP64 Linux (glibc and musl agree on the layout)
    private struct MsgHdr
    {
        public void* Name; public uint NameLen; private uint _pad0;
        public IoVec* Iov; public nuint IovLen;
        public void* Control; public nuint ControlLen;
        public int Flags; private int _pad1;
    }
    private struct IoVec { public void* Base; public nuint Len; }

    /// <summary>
    /// Returns decrypted bytes (&gt; 0), 0 at the end of the stream (FIN or a TLS alert such as close_notify), or -1 with
    /// errno set. Post-handshake handshake records (TLS 1.3 session tickets) are skipped.
    /// </summary>
    public static int Recv(int fd, byte* buf, int len)
    {
        byte* control = stackalloc byte[32]; // CMSG_SPACE(1) = 24
        while (true)
        {
            var iov = new IoVec { Base = buf, Len = (nuint)len };
            var msg = new MsgHdr { Iov = &iov, IovLen = 1, Control = control, ControlLen = 32 };
            nint n = Libc.recvmsg(fd, &msg, 0);
            if (n < 0) return -1;
            byte type = RecordApplicationData;
            if (msg.ControlLen >= 17 && *(int*)(control + 8) == SOL_TLS && *(int*)(control + 12) == TLS_GET_RECORD_TYPE)
                type = control[16];
            if (type == RecordApplicationData) return (int)n;
            if (type == RecordAlert) return 0;
            if (type == RecordHandshake) continue; // session ticket: not needed, skip
            return 0;
        }
    }
}
