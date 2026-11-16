# HybridMount.NET Privacy Policy

Publisher: RyleD. Text updated: November 16, 2026. This policy covers the Microsoft Store, sideloaded, and directory distributions of HybridMount.NET, which provide the same file synchronization features.

## Information processed

HybridMount.NET connects a WebDAV, S3-compatible storage, or Synology service that you choose to a local folder. To connect, read files, synchronize changes, and recover from errors, the app processes server addresses, mount paths, file and folder names, contents, sizes, timestamps, versions, permissions, task states, and the accounts, passwords, access keys, and tokens that you provide or authorize it to use.

The app does not require a publisher account. It has no feature that automatically uploads your files, accounts, or diagnostic logs to RyleD. Your selected storage services receive the information required for your operations. Those providers determine their own storage locations, retention practices, and policies.

## Connections and actions

Connecting, listing folders, downloading, and synchronizing local or remote changes communicate with your selected services. Automatic connection, remote detection, and SQS features can continue after the main window is hidden. Closing the window may only minimize the app to the system tray; exiting the app stops its background connections.

Connection tests use read-only queries. Detecting all capabilities may create, modify, and delete test resources in the selected remote folder. After cancellation, the app attempts cleanup and identifies resources that still require attention. Canceling does not automatically roll back a write already sent.

System DNS, proxies, certificate validation, and an AWS default credential chain that you choose may contact their respective services. The AWS SDK obtains authorization from configured environment settings, credential files, or role sources when you enable that credential chain. Microsoft Store, Windows, and your service providers may process installation, diagnostic, or service records under their own policies.

## Local storage and protection

Configuration, indexes, task and recovery records, file caches, thumbnails, and diagnostics are stored locally in locations accessible to the current Windows user. The mount folders you choose also contain files or placeholders. Saved service credentials are protected with DPAPI for the current Windows user; this does not encrypt all local files, caches, or logs.

Snapshots may be retained to protect content during uploads, conflicts, or uncertain outcomes. Ordinary application logs are pruned to at most 30 days and 10,000 entries; lifecycle diagnostics and recovery records follow separate retention rules. Logs and diagnostics may contain paths, server-related information, timestamps, errors, and process or user identifiers. Review them before sharing.

## Configuration backups and support data

An exported `.hmxbk` may contain server addresses, paths, usernames, passwords, and access keys. Without a backup password, credentials in that backup are plaintext. With a password, PBKDF2-SHA256 and AES-256-GCM protect the credential portion; server addresses, names, and other settings remain visible. A configuration backup does not contain file contents, synchronization indexes, or task recovery snapshots.

Check the server addresses and settings again when importing, even if the backup is password protected. Copying, exporting, or sharing logs and backups with support is an action you initiate. Share only what is needed and keep the backup password secure.

## Transport security

HTTPS is recommended. HTTPS uses the system's TLS capabilities and the app's certificate validation rules. If you use a certificate fingerprint, verify it through a trusted channel. When you explicitly choose HTTP, TLS does not protect that connection. The app cannot guarantee how your chosen servers, proxies, or other network participants protect data.

## Controls, retention, and deletion

You can disconnect, adjust automatic connection and detection, cancel tasks, remove mounts, and clear settings when the app's safety checks allow it. Read the effects of the removal option you select: preserving cached files does not download uncached content; discarding mount-point files deletes local contents under the root, including local changes. Removing a mount does not itself delete remote files.

Pending upload verification, conflicts, or incomplete removal may prevent cleanup so recovery information can be retained. Cancellation does not guarantee that a remote operation already sent is reversed. App cleanup does not delete backups that you exported separately or copies that you shared with others.

Before uninstalling, resolve pending tasks, save local content you need, and remove your mounts. Uninstalling may delete package configuration, credentials, caches, and recovery records. Placeholders that have not been downloaded require the service to be configured again before their contents can be read. Neither uninstalling nor caching is a file backup.

## Contact and changes

For questions about this policy or privacy concerns, contact RyleD using the support contact shown on the app's official Microsoft Store listing. You decide what information to provide in a support request.

The publisher will update this policy and its public explanation when data processing changes. The publisher cannot act on behalf of your chosen storage provider to delete remote accounts or remote data.
