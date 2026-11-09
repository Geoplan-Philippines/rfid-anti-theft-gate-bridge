/*************************************************************************************
File:    hax_7k_sdk.h
Purpose: Public interface contract of the HAX-7000 series device SDK (pure C exports).
         Intended for callers, and can also be invoked from C# via P/Invoke.

Conventions:
  - Pure C interface: extern "C" + __stdcall, export names are undecorated so that
    C# DllImport can bind directly.
  - All strings are UTF-8 encoded.
  - Except Device_GetVersion, all functions return an int error code: 0 = success,
    non-zero see device_error_code.
  - Fixed call order: Device_Init -> Device_RegisterCallback -> Device_StartScan
                      -> (callback) -> Device_StopScan -> Device_Dispose
                      (StartScan/StopScan may be called repeatedly).

Note: This header depends on no other header (__stdcall is an MSVC keyword and needs no include).
*************************************************************************************/
/*************************************************************************************
Usage of the packet APIs (Device_Pack* / Device_UnPack*), see codes/main/test_svr/main.cpp.
These APIs only build / parse packets; the caller owns the TCP connection (no port is opened).

  1. Load card_7k.dll (x86). The exports are pure C and undecorated, so they can be bound via
     GetProcAddress (see test_svr) or P/Invoke DllImport in C#.

  2. After a device (client) connects, enable server control:
       Device_PackServerControl(1, buf, 256, &len)   -> send buf[0..len) to the device
       recv the reply (one recv, up to 256 bytes)    -> Device_UnPackServerControl(reply, reply_len)
       Continue only when the return value is DEVICE_OK (0), otherwise stop and report the error code.

  3. Afterwards the device keeps reporting EPC data. For every recv (up to 10240 bytes):
       char json[10240]; int json_len = 0;
       ret = Device_UnPackEpcId(data, n, json, sizeof(json), &json_len);
         DEVICE_OK                   -> json holds e.g. [{"ant_id":1,"epc_id":"E2000017..."}]
         DEVICE_ERR_BUFFER_TOO_SMALL -> enlarge json to json_len bytes and call again
         other                       -> see device_error_code

  4. Optional, only when device alarm is needed, enable the alarm channels:
       struct device_alarm_config cfg = {1, 1, 1, 1};   (0 = disabled, 1 = enabled)
       Device_PackDeviceAlarm(&cfg, buf, 256, &len)  -> send buf[0..len) to the device
       recv the reply (one recv, up to 256 bytes)    -> Device_UnPackDeviceAlarm(reply, reply_len)
       Continue only when the return value is DEVICE_OK (0).

Notes:
  - Packets have no frame header / length field: one recv is treated as one packet.
  - Always check the return value of every Pack / UnPack call; do not skip a step when its
    reply was not accepted.
*************************************************************************************/
#ifndef HAX_7K_SDK_H
#define HAX_7K_SDK_H

#if defined(_WIN32)
#  define HAX_7K_CALL __stdcall
#else
#  define HAX_7K_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Standard error codes; functions return these numeric values */
enum device_error_code {
  DEVICE_OK = 0,                   /* Success */
  DEVICE_ERR_INVALID_PARAM = 1,    /* Invalid parameter */
  DEVICE_ERR_BUFFER_TOO_SMALL = 2, /* Output buffer too small */
  DEVICE_ERR_PACK_FAILED = 3,      /* Pack failed */
  DEVICE_ERR_UNPACK_FAILED = 4,    /* Unpack failed (header/length/CRC check failed) */
  DEVICE_ERR_FLAG_FAIL = 10,       /* Device returned flag=0: failure */
  DEVICE_ERR_FLAG_ABNORMAL = 12,   /* Device returned flag=2: abnormal */
  DEVICE_ERR_FLAG_DUPLICATE = 13,  /* Device returned flag=3: duplicate */
  DEVICE_ERR_FLAG_UNKNOWN = 14,    /* Device returned unknown flag */
  DEVICE_ERR_NETWORK = 20,         /* EPC verification could not reach the server (communication error) */
};

/* Unpack: decrypts and verifies the device reply in buf. flag=1 returns DEVICE_OK;
   flag=0/2/3 return the corresponding error codes above. */
int HAX_7K_CALL Device_UnPackServerControl(unsigned char *buf, int buf_size);

/* Pack only: builds the set_enable_server_control command.
   value: must be 0 or 1.
   out_buf/out_buf_size: caller-provided buffer; receives the (encrypted) raw packet that can be sent to the device
   as-is. out_len: actual number of output bytes. Does not open a port or send anything. */
int HAX_7K_CALL Device_PackServerControl(int value, unsigned char *out_buf, int out_buf_size, int *out_len);

/* Device alarm channel switches: 0 = disabled, 1 = enabled (maps to device_alarm_st bits 0..3) */
struct device_alarm_config {
  unsigned char channel_1;
  unsigned char channel_2;
  unsigned char channel_3;
  unsigned char channel_4;
};

/* Device alarm enable. Call this api only when device alarm is needed; it builds the
   set_enable_device_alarm command.
   Each channel value must be 0 or 1. Output semantics are the same as Device_PackServerControl. */
int HAX_7K_CALL Device_PackDeviceAlarm(const struct device_alarm_config *config, unsigned char *out_buf,
                                       int out_buf_size,
                                       int *out_len); /* the corresponding unpack: Device_UnPackDeviceAlarm */

/* Unpack: verifies the reply of the command built by Device_PackDeviceAlarm;
   same return rules as Device_UnPackServerControl. Only needed after Device_PackDeviceAlarm was sent. */
int HAX_7K_CALL Device_UnPackDeviceAlarm(unsigned char *buf, int buf_size);

/* Unpack the read_epc_id reply: decrypts, verifies and parses buf.
   On success writes a NUL-terminated UTF-8 JSON array into json_buf, e.g.
   [{"ant_id":1,"epc_id":"E2000017..."}], and returns DEVICE_OK.
   json_buf/json_buf_size: caller-provided buffer, recommended size 10240.
   json_len: receives the JSON string length (excluding the NUL terminator). If json_buf is too small,
   DEVICE_ERR_BUFFER_TOO_SMALL is returned and json_len receives the required buffer size (including the NUL).
   Other failures return the matching error code. */
int HAX_7K_CALL Device_UnPackEpcId(unsigned char *buf, int buf_size, char *json_buf, int json_buf_size, int *json_len);

#ifdef __cplusplus
}
#endif

#endif /* HAX_7K_SDK_H */
