# .hmxbk 配置备份

从0.0.13.0开始，导入和导出只使用`.hmxbk`，内容为UTF-8 JSON。文件扩展名不区分大小写；可以读取编辑器添加的UTF-8 BOM，程序导出不写BOM。无密码时全部明文；有密码时仅凭据加密。非敏感部分不提供完整性保证，允许手工编辑；导入后先检查实际服务器、协议和目录再手动连接。

## 无密码示例

以下是单个WebDAV配置的示例，用户名和密码均为示例值。`backupEntryId`仅用于备份内关联，导入时不作为挂载或同步根身份。

```json
{
  "format": "hmxbk",
  "schemaVersion": 1,
  "exportedUtc": "2026-11-12T00:00:00Z",
  "protection": { "mode": "none" },
  "mounts": [
    {
      "backupEntryId": "60dc9d7e-5efe-463a-9aa3-a9c0eed427fa",
      "name": "WebDAV 示例",
      "localPath": "D:\\ExampleMount",
      "source": {
        "kind": "webDav",
        "endpoint": "https://dav.example.test/dav/",
        "bucket": "",
        "prefix": "",
        "region": "us-east-1",
        "pathStyle": true,
        "allowHttp": false,
        "useDefaultCredentials": false
      },
      "autoConnect": true,
      "thumbnailsEnabled": false,
      "remoteAutoDetectEnabled": true,
      "remoteIncrementalSeconds": 60,
      "remoteFullSeconds": 300,
      "capacityAutoDetectEnabled": false,
      "exclusionRules": [ { "match": "prefix", "text": "~" } ],
      "credentials": {
        "userName": "example-user",
        "password": "example-password",
        "accessKey": "",
        "secretKey": "",
        "sessionToken": ""
      }
    }
  ]
}
```

完整备份还可包含`maximumCapacityBytes`、`sqs`、`sqsCredentials`以及`source.pinnedCertificateSha256`等现有用户选项。枚举使用camelCase字符串。字段名区分大小写；重复字段、未知字段、未知版本和超限输入被拒绝。每份文件最多8 MiB、1000个挂载，JSON深度最多16。

## 有密码格式

`protection`包含`mode: "password"`、`kdf: "PBKDF2-SHA256"`、`iterations: 600000`、16字节随机`salt`及`cipher: "AES-256-GCM"`。以不修剪、不正规化的UTF-8密码派生32字节密钥；每次导出使用新盐。

各挂载省略`credentials`和`sqsCredentials`，改为`protectedCredentials`，包含`nonce`、`ciphertext`、`tag`。这些字段及盐使用标准Base64。每个块使用不同的随机12字节nonce、16字节GCM标签。明文载荷是`{"credentials": {...}, "sqsCredentials": {...}}`，没有独立SQS凭据时省略后者。

GCM附加认证数据为UTF-8字符串`hmxbk|1|<backupEntryId的小写D格式GUID>|credentials`。条目标识和凭据块需一起保留；交换单独的密文块或改变标识会导致认证失败。更改名称、本地目录、服务器地址或排除规则不会使凭据解密失效。密码至少8个字符，空白字符参与派生；没有公钥、私钥或机器专属备份密钥，仅凭文件和密码即可迁移。

用户名、密码、S3/SQS访问密钥、秘密密钥和会话令牌均包含在凭据保护中。SDK默认凭据链只保存模式，不解析或复制机器上实际凭据；群晖设备令牌不导出。无论备份是否加密，导入到本机后凭据都重新保存为当前用户DPAPI密文。

## 导入与旧备份迁移

无保护文件直接进入配置预览；密码文件先要求密码，成功后进入同一预览。导入仅新增、重复项跳过、重叠或无效目录不可提交，不覆盖现有挂载，不注册或连接远端。备份中的原目录仅为提示，可以选择本机NTFS空目录。文件读取后若被编辑，本次对话框仍使用原快照；关闭后重新选择才能读取修改。

旧`.hymcfg`／`.hmconfig`以及HMCONFIG v1—v6容器不再支持，改扩展名也无效。先用0.0.12.0读取旧备份并恢复为本机配置，再用0.0.13.0导出新格式。本机已有配置可以直接使用，无需转换；程序不自动改写旧备份。
