/*
 * hax_7k_sdk demo (plain C style)
 *
 * A TCP server that drives the Device_Pack* / Device_UnPack* exports of card_7k.dll:
 *   1. ask ip / port, listen and accept
 *   2. send Device_PackServerControl packet, the client reply must pass Device_UnPackServerControl (ret 0)
 *   3. every following client data is decoded by Device_UnPackEpcId and printed as json
 *
 * After every epc id packet is verified successfully, the device alarm step (Device_PackDeviceAlarm /
 * Device_UnPackDeviceAlarm) is run.
 *
 * Framing: there is no frame header / length field. Replies of step 2 are read with one
 * recv() of up to 256 bytes, epc data with one recv() of up to 10240 bytes.
 */
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#pragma comment(lib, "ws2_32.lib")
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "../../modules/hax_uhf/inc/hax_7k_sdk.h"

#define DEFAULT_IP "0.0.0.0"
#define DEFAULT_PORT 9000

#define PACKET_BUF_SIZE 256 /* packed command / client reply (first packets) */
#define EPC_BUF_SIZE 10240  /* epc id data */
#define JSON_BUF_SIZE 10240
#define REPLY_TIMEOUT_MS 5000

/* function pointer types matching hax_7k_sdk.h */
typedef int(HAX_7K_CALL *pfunc_PackServerControl)(int value, unsigned char *out_buf, int out_buf_size,
                                                  int *out_len);
typedef int(HAX_7K_CALL *pfunc_UnPackServerControl)(unsigned char *buf, int buf_size);
typedef int(HAX_7K_CALL *pfunc_PackDeviceAlarm)(const struct device_alarm_config *config, unsigned char *out_buf,
                                                int out_buf_size, int *out_len);
typedef int(HAX_7K_CALL *pfunc_UnPackDeviceAlarm)(unsigned char *buf, int buf_size);
typedef int(HAX_7K_CALL *pfunc_UnPackEpcId)(unsigned char *buf, int buf_size, char *json_buf, int json_buf_size,
                                            int *json_len);

/* filled by sdk_load() */
static HMODULE g_sdk_module = NULL;
static pfunc_PackServerControl g_pack_server_control = NULL;
static pfunc_UnPackServerControl g_unpack_server_control = NULL;
static pfunc_PackDeviceAlarm g_pack_device_alarm = NULL;
static pfunc_UnPackDeviceAlarm g_unpack_device_alarm = NULL;
static pfunc_UnPackEpcId g_unpack_epc_id = NULL;

/* ------------------------------------------------------------------------------------------
 * helpers
 * ---------------------------------------------------------------------------------------- */

static const char *error_text(int code) {
  switch (code) {
    case DEVICE_OK: return "ok";
    case DEVICE_ERR_INVALID_PARAM: return "invalid param";
    case DEVICE_ERR_BUFFER_TOO_SMALL: return "buffer too small";
    case DEVICE_ERR_PACK_FAILED: return "pack failed";
    case DEVICE_ERR_UNPACK_FAILED: return "unpack failed (header/length/crc)";
    case DEVICE_ERR_FLAG_FAIL: return "device flag=0 (fail)";
    case DEVICE_ERR_FLAG_ABNORMAL: return "device flag=2 (abnormal)";
    case DEVICE_ERR_FLAG_DUPLICATE: return "device flag=3 (duplicate)";
    case DEVICE_ERR_FLAG_UNKNOWN: return "device flag unknown";
    case DEVICE_ERR_NETWORK: return "epc verify: server unreachable";
    default: return "unknown";
  }
}

static void print_hex(const char *tag, const unsigned char *buf, int len) {
  int i;
  printf("%s %d bytes:", tag, len);
  for (i = 0; i < len; ++i) printf(" %02X", buf[i]);
  printf("\n");
}

/* read one line from stdin, use default_text when the line is empty */
static void read_line(const char *prompt, const char *default_text, char *out, int out_size) {
  printf("%s [%s]: ", prompt, default_text);
  if (fgets(out, out_size, stdin) == NULL) out[0] = '\0';
  out[strcspn(out, "\r\n")] = '\0';
  if (out[0] == '\0') {
    strncpy(out, default_text, out_size - 1);
    out[out_size - 1] = '\0';
  }
}

/* ------------------------------------------------------------------------------------------
 * sdk loading
 * ---------------------------------------------------------------------------------------- */

/* load card_7k.dll and resolve the exports used by the demo, returns 1 on success */
static int sdk_load(void) {
  g_sdk_module = LoadLibraryW(L"card_7k.dll");
  if (g_sdk_module == NULL) {
    printf("LoadLibrary card_7k.dll failed: %lu\n", GetLastError());
    return 0;
  }
  g_pack_server_control = (pfunc_PackServerControl)GetProcAddress(g_sdk_module, "Device_PackServerControl");
  g_unpack_server_control = (pfunc_UnPackServerControl)GetProcAddress(g_sdk_module, "Device_UnPackServerControl");
  g_pack_device_alarm = (pfunc_PackDeviceAlarm)GetProcAddress(g_sdk_module, "Device_PackDeviceAlarm");
  g_unpack_device_alarm = (pfunc_UnPackDeviceAlarm)GetProcAddress(g_sdk_module, "Device_UnPackDeviceAlarm");
  g_unpack_epc_id = (pfunc_UnPackEpcId)GetProcAddress(g_sdk_module, "Device_UnPackEpcId");
  if (!g_pack_server_control || !g_unpack_server_control || !g_pack_device_alarm || !g_unpack_device_alarm ||
      !g_unpack_epc_id) {
    printf("GetProcAddress failed\n");
    return 0;
  }
  return 1;
}

static void sdk_unload(void) {
  if (g_sdk_module != NULL) FreeLibrary(g_sdk_module);
  g_sdk_module = NULL;
}

/* ------------------------------------------------------------------------------------------
 * socket helpers
 * ---------------------------------------------------------------------------------------- */

static void set_recv_timeout(SOCKET s, DWORD timeout_ms) {
  setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&timeout_ms, sizeof(timeout_ms));
}

/* send the whole buffer, returns 1 on success */
static int send_all(SOCKET s, const unsigned char *data, int len) {
  int sent = 0;
  while (sent < len) {
    int n = send(s, (const char *)data + sent, len - sent, 0);
    if (n <= 0) return 0;
    sent += n;
  }
  return 1;
}

/* one recv() with the given timeout (0 = wait forever), returns received length, <=0 on failure */
static int recv_once(SOCKET s, unsigned char *buf, int buf_size, DWORD timeout_ms) {
  int n;
  set_recv_timeout(s, timeout_ms);
  n = recv(s, (char *)buf, buf_size, 0);
  if (n > 0) print_hex("[recv]", buf, n);
  return n;
}

/* ------------------------------------------------------------------------------------------
 * protocol steps; each returns 1 when the client acknowledged with unpack ret == 0
 * ---------------------------------------------------------------------------------------- */

static int step_server_control(SOCKET client) {
  unsigned char packet[PACKET_BUF_SIZE];
  unsigned char reply[PACKET_BUF_SIZE];
  int packet_len = 0;
  int reply_len;
  int ret;

  ret = g_pack_server_control(1, packet, sizeof(packet), &packet_len);
  if (ret != DEVICE_OK) {
    printf("Device_PackServerControl ret=%d (%s)\n", ret, error_text(ret));
    return 0;
  }
  print_hex("[send] server_control", packet, packet_len);
  if (!send_all(client, packet, packet_len)) return 0;

  reply_len = recv_once(client, reply, sizeof(reply), REPLY_TIMEOUT_MS);
  if (reply_len <= 0) {
    printf("no server_control reply\n");
    return 0;
  }
  ret = g_unpack_server_control(reply, reply_len);
  printf("[unpack] Device_UnPackServerControl ret=%d (%s)\n", ret, error_text(ret));
  return ret == DEVICE_OK;
}

/* device alarm: called after every epc id packet that has been verified successfully */
static int step_device_alarm(SOCKET client) {  struct device_alarm_config alarm = {1, 1, 1, 1};
  unsigned char packet[PACKET_BUF_SIZE];
  unsigned char reply[PACKET_BUF_SIZE];
  int packet_len = 0;
  int reply_len;
  int ret;

  ret = g_pack_device_alarm(&alarm, packet, sizeof(packet), &packet_len);
  if (ret != DEVICE_OK) {
    printf("Device_PackDeviceAlarm ret=%d (%s)\n", ret, error_text(ret));
    return 0;
  }
  print_hex("[send] device_alarm", packet, packet_len);
  if (!send_all(client, packet, packet_len)) return 0;

  reply_len = recv_once(client, reply, sizeof(reply), REPLY_TIMEOUT_MS);
  if (reply_len <= 0) {
    printf("no device_alarm reply\n");
    return 0;
  }
  ret = g_unpack_device_alarm(reply, reply_len);
  printf("[unpack] Device_UnPackDeviceAlarm ret=%d (%s)\n", ret, error_text(ret));
  return ret == DEVICE_OK;
}

/* receive epc data until the client disconnects and print the unpacked json;
   the device alarm is sent after every successful epc verification (json not empty) */
static void step_receive_epc(SOCKET client) {
  static unsigned char data[EPC_BUF_SIZE];
  static char json[JSON_BUF_SIZE];

  while (1) {
    int json_len = 0;
    int ret;
    int n = recv_once(client, data, sizeof(data), 0);
    if (n <= 0) {
      printf("client closed\n");
      return;
    }

    ret = g_unpack_epc_id(data, n, json, sizeof(json), &json_len);
    if (ret == DEVICE_OK) {
      printf("[unpack] epc json: %.*s\n", json_len, json);
      if (json_len > 2) {
        step_device_alarm(client);
      } else {
        printf("[unpack] empty json (uid rejected or no tag), device alarm not sent\n");
      }
    } else if (ret == DEVICE_ERR_BUFFER_TOO_SMALL) {
      printf("[unpack] json buffer too small, need %d bytes\n", json_len);
    } else {
      printf("[unpack] Device_UnPackEpcId ret=%d (%s)\n", ret, error_text(ret));
    }
  }
}

static void serve_client(SOCKET client) {
  if (!step_server_control(client)) return;
  step_receive_epc(client);
}

/* ------------------------------------------------------------------------------------------
 * tcp server
 * ---------------------------------------------------------------------------------------- */

/* create a listening socket on ip:port, returns INVALID_SOCKET on failure */
static SOCKET create_listen_socket(const char *ip, int port) {
  struct sockaddr_in addr;
  BOOL reuse = TRUE;
  char addr_text[96];
  SOCKET s;
  int addr_len = sizeof(addr);

  /* WSAStringToAddress parses "ip:port" (works on old _WIN32_WINNT where inet_pton is missing) */
  memset(&addr, 0, sizeof(addr));
  sprintf(addr_text, "%s:%d", ip, port);
  if (WSAStringToAddressA(addr_text, AF_INET, NULL, (struct sockaddr *)&addr, &addr_len) != 0) {
    printf("invalid ip: %s\n", ip);
    return INVALID_SOCKET;
  }

  s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
  if (s == INVALID_SOCKET) return INVALID_SOCKET;
  setsockopt(s, SOL_SOCKET, SO_REUSEADDR, (const char *)&reuse, sizeof(reuse));

  if (bind(s, (struct sockaddr *)&addr, sizeof(addr)) == SOCKET_ERROR || listen(s, SOMAXCONN) == SOCKET_ERROR) {
    printf("listen on %s:%d failed, err=%d\n", ip, port, WSAGetLastError());
    closesocket(s);
    return INVALID_SOCKET;
  }
  return s;
}

/* clients are served one at a time: the next one waits in the backlog until the current one leaves */
static void run_server(void) {
  char ip[64];
  char port_text[16];
  int port;
  WSADATA wsa;
  SOCKET listen_sock;

  read_line("input ip", DEFAULT_IP, ip, sizeof(ip));
  read_line("input port", "9000", port_text, sizeof(port_text));
  port = atoi(port_text);
  if (port < 1 || port > 65535) port = DEFAULT_PORT;

  if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
    printf("WSAStartup failed\n");
    return;
  }

  listen_sock = create_listen_socket(ip, port);
  if (listen_sock != INVALID_SOCKET) {
    printf("listening on %s:%d ...\n", ip, port);
    while (1) {
      struct sockaddr_in peer;
      char peer_text[96] = {0};
      DWORD peer_text_len = sizeof(peer_text);
      int peer_len = sizeof(peer);
      SOCKET client = accept(listen_sock, (struct sockaddr *)&peer, &peer_len);
      if (client == INVALID_SOCKET) break;

      WSAAddressToStringA((struct sockaddr *)&peer, peer_len, NULL, peer_text, &peer_text_len);
      printf("client connected: %s\n", peer_text);
      serve_client(client);
      closesocket(client);
    }
    closesocket(listen_sock);
  }
  WSACleanup();
}

int main(int argc, char *argv[]) {
  setvbuf(stdout, NULL, _IONBF, 0);
  if (!sdk_load()) return 1;
  run_server();
  sdk_unload();
  return 0;
}
