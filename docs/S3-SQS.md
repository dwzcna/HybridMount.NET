# AWS S3 的可选 SQS 事件加速

0.0.8 默认通过每五分钟完整对账检测 S3 变化，SQS 默认关闭。R2 和其他 S3 兼容服务使用定时对账；Cloudflare Queues 尚未接入。开启 SQS 后完整对账仍保留，可在挂载页调整周期。

## 配置

1. 在与 S3 桶相同的区域创建 **Standard Queue**。每台机器使用独立专用队列，多台机器竞争同一队列会分别取得消息并漏提示；同进程的多个挂载可共享一队列和有效队列凭据。本版不支持 FIFO、SNS 包装或 Cloudflare Queues。
2. 为队列配置死信队列（DLQ）及适当的最大接收次数。畸形、越界或不属于已订阅挂载的消息不确认，避免悄悄丢失；接收器会提示检查配置，DLQ 用于保存持续无法处理的消息。
3. 允许 S3 服务向队列发送消息。将下例的区域、账号、桶和队列替换为自己的值，并保留已有必要策略：

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": {"Service": "s3.amazonaws.com"},
    "Action": "sqs:SendMessage",
    "Resource": "arn:aws:sqs:REGION:ACCOUNT:QUEUE",
    "Condition": {
      "ArnEquals": {"aws:SourceArn": "arn:aws:s3:::BUCKET"},
      "StringEquals": {"aws:SourceAccount": "ACCOUNT"}
    }
  }]
}
```

4. 在桶通知中**新增**发送到该队列的 `s3:ObjectCreated:*` 和 `s3:ObjectRemoved:*` 通知，前缀与挂载范围一致。保留已有的通知及其他使用方配置；本程序不创建队列、不修改或覆盖桶通知。桶通知接口会提交整个配置，手动设置时也应先保留原配置。
5. 在挂载页启用“远端自动检测”，打开“SQS 事件加速”，填入队列 URL、相同区域及凭据来源。可以明确复用 S3 凭据、使用 SDK 默认凭据链，或独立访问密钥／临时令牌。S3 权限不代表已有队列权限；独立密钥以当前用户 DPAPI 保存在 Mounting/Sqs/Credentials，只随密码加密的 v3 备份迁移。
6. 点击“测试连接”只读取队列属性并验证身份／类型，**不会接收、删除消息或探测写入**。运行时还需要 `sqs:ReceiveMessage`、`sqs:DeleteMessage`，属性测试需要 `sqs:GetQueueAttributes`；队列使用 KMS 时还需相应 KMS 解密权限。权限范围应限定到本机专用队列 ARN。

## 运行边界

- 20 秒长轮询；同进程同队列只创建一个接收器，使用一致的有效队列凭据。改变其中一个挂载的凭据而其他订阅仍使用不同凭据时会提示，定时完整对账继续。
- 验证 bucket、区域和 prefix，Key 按 AWS 表单 URL 编码仅解码一次，`+` 是空格，`%2B` 是加号。sequencer 仅在同一桶／Key 比较；事件中的 VersionId 或删除消息不能直接决定本地删除。
- 所有相关记录在 SQLite v6 可靠落库后才确认消息；ReceiptHandle 和原始正文不落盘。确认响应丢失重投时保持幂等。当前 S3 元数据查询后产生可重放的待应用事件，占用／修改／冲突继续保护。
- S3 测试事件可以确认而不修改本地内容。消息无效或不属于当前挂载时不确认；配置专用队列及通知前缀以避免无关消息反复投递。
- 队列网络故障退避 1–15 分钟，认证／权限失败暂停至重新连接／更正设置；S3 完整对账独立运行。断开／退出／移除取消接收及扫描，未落库消息不确认。
- 完整对账不是跨多个请求的原子快照。扫描期间对象变化会立即重扫，持续变化时保留现有文件并继续检测；不会盲目重发写入，也不会因失败列表清空本地。

## 参考资料（不作为真实服务验收）

- https://docs.aws.amazon.com/AmazonS3/latest/userguide/notification-content-structure.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/notification-how-to-event-types-and-destinations.html
- https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-long-polling.html
- https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-dead-letter-queues.html
- https://www.nuget.org/packages/AWSSDK.SQS/4.0.100.21
