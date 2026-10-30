# 预览测试媒体

sample.jpg、sample.png 和 sample.mp4 是本项目使用 Windows WIC／Media Foundation 生成的纯色合成样本，不包含用户数据或第三方媒体。

- JPEG／PNG：64×32，BGRA=(40,90,180,255)，WIC 对应格式编码器。
- MP4：128×128，30fps，H.264，2 帧，无音轨；NV12 输入 Y=100、UV=128，码率 200000，像素比例 1:1，渐进帧。
- 生成流程：MFCreateSinkWriterFromURL → H.264 输出类型 → NV12 输入类型 → BeginWriting → WriteSample 两次 → Finalize；图片由 WIC 从相同固定像素生成。

生成辅助程序位于本机忽略的 patch 目录，不构成第四个产品工程。样本直接随测试复制，运行测试无需编码器、网络或外部工具。
