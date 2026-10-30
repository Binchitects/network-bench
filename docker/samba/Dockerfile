# Samba serving a tmpfs mount (/data) - SMB2/SMB3 with sendfile, recvfile, multichannel
FROM alpine:3.22
RUN apk add --no-cache samba-server samba-common-tools tini
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
ENV SMB_PORT=445 SMB_SHARE=bench SMB_USER=bench SMB_PASS=bench SMB_GUEST=yes SMB_FILES=1G,10G SMB_ENCRYPT=default
EXPOSE 445
HEALTHCHECK --interval=10s --timeout=3s CMD nc -z 127.0.0.1 "$SMB_PORT" || exit 1
ENTRYPOINT ["/sbin/tini", "--", "/entrypoint.sh"]
