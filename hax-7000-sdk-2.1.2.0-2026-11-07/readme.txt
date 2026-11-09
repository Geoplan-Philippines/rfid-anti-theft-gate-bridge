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