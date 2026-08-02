# MCCP 更新服务器

服务只监听 `127.0.0.1:18080`，公网请求应通过部署者自己的 HTTPS Nginx
反向代理进入。示例配置使用 `updates.example.com`，部署前必须替换为真实域名。

## 路径

- 服务程序：`/opt/mccp-update-server/mccp_update_server.py`
- 数据：`/var/lib/mccp-update`
- 发布密钥：`/etc/mccp-update/publisher.key`
- systemd：`mccp-update.service`
- Nginx：`/etc/nginx/sites-available/mccp-update`

## 密钥管理

```bash
sudo mccp-update-key show
sudo mccp-update-key rotate
sudo mccp-update-key export /root/mccp-publisher.key
```

`export` 不会覆盖现有文件。也可以通过 SSH/SFTP 直接下载
`/etc/mccp-update/publisher.key`。密钥只用于打包器发布更新，绝不能放进
Launcher、安装包、项目配置或日志。执行 `rotate` 后旧密钥立即失效。

## HTTP 接口

- `GET /v1/health`：健康检查。
- `GET /v1/products/{productId}/manifest`：指定产品的当前版本清单；无版本时返回 503。
- `GET /v1/files/{productId}/{releaseId}/{path}`：下载清单中的文件。
- `GET /v1/launchers/{productId}/{version}/setup.exe`：下载不可变的启动器安装包。
- `POST /v1/publish`：打包器发布完整更新包。
- `POST /v1/products/{productId}/launcher`：发布更高版本的启动器安装包。
- `POST /v1/products/{productId}/policy`：使用发布密钥更新客户端公告与启动限制。

发布请求使用密钥文件计算 HMAC-SHA256。服务器在读取请求正文前检查签名、
时间戳和一次性随机数，归档文件本身再按 SHA-256 复核。发布内容先在独立目录
完成路径、大小和哈希校验，成功后才原子切换当前清单。

服务器验证并解压打包器上传的 ZIP 后，只保留清单和逐文件目录，不再额外生成
`bundle.tar.gz`。Launcher 根据清单以最多 200 路连接并行请求逐个文件，服务端
无需再次压缩或校验数 GB 的流式包。新版本完成校验并原子切换当前清单后，服务器
会立即删除同一产品的全部旧 MC release；发布失败时仍保留当前 release，并清理
未提交的新 release。服务器只需为“当前版本 + 上传 ZIP + 新版本解压目录”预留
发布峰值空间。

启动器安装包发布还必须提供 `X-MCCP-Launcher-Version: x.y.z`。服务器拒绝
与当前版本相同或更低的版本，安装包保存在
`/var/lib/mccp-update/launchers/{productId}/{version}/setup.exe`，当前启动器
元数据保存在 `/var/lib/mccp-update/launcher-current/{productId}.json`。产品
清单会动态附加 `launcher` 字段；客户端在应用 MC 更新前先完成 Launcher 更新。
启动器新版本原子切换成功后，同一产品的旧启动器版本目录也会立即删除。
相同版本、大小和 SHA-256 的启动器重复发布按幂等成功处理，便于 MC 包上传失败后
直接重试；相同版本但内容不同仍会拒绝。
轻量安装包只覆盖 Launcher 和更新引导配置，不包含也不会删除 `.minecraft`。

MC 文件清单中的 `preserveExisting` 标记用于保护用户数据。客户端还会对存档、
配置、截图、资源包、光影包、服务器列表及常见地图/路径点目录执行本地强制保护，
即使服务端清单缺少标记，也不会覆盖已经存在的用户文件。未被旧清单管理的用户
新增文件不会进入删除集合。

每次获取产品清单时，服务器会动态附加该产品的 `policy`。策略文件保存在
`/var/lib/mccp-update/policies/{productId}.json`，可以独立于整合包版本更新。
策略支持自定义弹窗标题、正文、是否显示公告以及是否禁止启动游戏。策略损坏
时服务会采用故障关闭方式，向客户端返回配置异常提示并禁止启动。

## TLS 证书

示例 Nginx 配置假定证书位于
`/etc/letsencrypt/live/updates.example.com/`。部署者应使用自己的域名申请可信
证书，并执行 `certbot renew --dry-run` 验证自动续期。生产环境不得关闭客户端
证书验证，也不得把私钥、发布密钥或导出的密钥副本提交到本仓库。
