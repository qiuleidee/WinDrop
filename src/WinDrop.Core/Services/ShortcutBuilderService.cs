using System.Security;
using System.Text;

namespace WinDrop.Core.Services;

/// <summary>
/// 拆分版专属 iOS 快捷指令（零菜单直达 + 智能分类归档）生成器
/// 生成符合 Apple Workflow Plist 规范的完整动作流，并提供 macOS / GitHub Actions 官方自动签名脚本
/// </summary>
public static class ShortcutBuilderService
{
    private const string ActionGetContentsOfUrl = "is.workflow.actions.downloadurl";
    private const string ActionNotification = "is.workflow.actions.notification";
    private const string ActionGetDictionaryValue = "is.workflow.actions.getvalueforkey";
    private const string ActionConditional = "is.workflow.actions.conditional";
    private const string ActionOpenUrl = "is.workflow.actions.openurl";
    private const string ActionSetClipboard = "is.workflow.actions.setclipboard";
    private const string ActionRepeatEach = "is.workflow.actions.repeat.each";
    private const string ActionSaveToPhotoAlbum = "is.workflow.actions.savetocameraroll";
    private const string ActionSaveFile = "is.workflow.actions.documentpicker.save";
    private const string RangeZeroOneKey = "{0, 1}";

    /// <summary>
    /// 生成「发送到电脑（共享表单自动发）」完整动作流 Plist
    /// 动作流：共享表单输入 -> POST /v2/send (Multipart Form: file) -> 显示结果通知
    /// </summary>
    public static byte[] BuildSendToWindowsShortcutPlist(string host, int port, string key)
    {
        string sendUrl = SecurityElement.Escape($"http://{host}:{port}/v2/send")!;
        string escapedKey = SecurityElement.Escape(key)!;
        string repeatGroupId = Guid.NewGuid().ToString().ToUpperInvariant();
        string httpActionUuid = Guid.NewGuid().ToString().ToUpperInvariant();

        string xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>WFWorkflowClientVersion</key>
                <string>2302.0.4</string>
                <key>WFWorkflowMinimumClientVersion</key>
                <integer>900</integer>
                <key>WFWorkflowMinimumClientVersionString</key>
                <string>900</string>
                <key>WFWorkflowHasOutputFallback</key>
                <false/>
                <key>WFWorkflowHasShortcutInputVariables</key>
                <true/>
                <key>WFWorkflowImportQuestions</key>
                <array/>
                <key>WFWorkflowIcon</key>
                <dict>
                    <key>WFWorkflowIconStartColor</key>
                    <integer>463140863</integer>
                    <key>WFWorkflowIconGlyphNumber</key>
                    <integer>59784</integer>
                </dict>
                <key>WFWorkflowTypes</key>
                <array>
                    <string>ActionExtension</string>
                </array>
                <key>WFWorkflowInputContentItemClasses</key>
                <array>
                    <string>WFImageContentItem</string>
                    <string>WFAVAssetContentItem</string>
                    <string>WFGenericFileContentItem</string>
                    <string>WFURLContentItem</string>
                    <string>WFStringContentItem</string>
                </array>
                <key>WFWorkflowOutputContentItemClasses</key>
                <array/>
                <key>WFWorkflowActions</key>
                <array>
                    <dict>
                        <key>WFWorkflowActionIdentifier</key>
                        <string>{ActionRepeatEach}</string>
                        <key>WFWorkflowActionParameters</key>
                        <dict>
                            <key>GroupingIdentifier</key>
                            <string>{repeatGroupId}</string>
                            <key>WFControlFlowMode</key>
                            <integer>0</integer>
                            <key>WFInput</key>
                            <dict>
                                <key>Value</key>
                                <dict><key>Type</key><string>ExtensionInput</string></dict>
                                <key>WFSerializationType</key>
                                <string>WFTextTokenAttachment</string>
                            </dict>
                        </dict>
                    </dict>
                    <dict>
                        <key>WFWorkflowActionIdentifier</key>
                        <string>{ActionGetContentsOfUrl}</string>
                        <key>WFWorkflowActionParameters</key>
                        <dict>
                            <key>UUID</key>
                            <string>{httpActionUuid}</string>
                            <key>Advanced</key>
                            <true/>
                            <key>WFHTTPMethod</key>
                            <string>POST</string>
                            <key>WFHTTPBodyType</key>
                            <string>Form</string>
                            <key>WFURL</key>
                            <string>{sendUrl}</string>
                            <key>WFHTTPHeaders</key>
                            <dict>
                                <key>Value</key>
                                <dict>
                                    <key>WFDictionaryFieldValueItems</key>
                                    <array>
                                        {BuildDictTextItem("Authorization", escapedKey)}
                                    </array>
                                </dict>
                                <key>WFSerializationType</key>
                                <string>WFDictionaryFieldValue</string>
                            </dict>
                            <key>WFFormValues</key>
                            <dict>
                                <key>Value</key>
                                <dict>
                                    <key>WFDictionaryFieldValueItems</key>
                                    <array>
                                        <dict>
                                            <key>WFItemType</key>
                                            <integer>5</integer>
                                            <key>WFKey</key>
                                            <dict>
                                                <key>Value</key>
                                                <dict>
                                                    <key>attachmentsByRange</key>
                                                    <dict/>
                                                    <key>string</key>
                                                    <string>file</string>
                                                </dict>
                                                <key>WFSerializationType</key>
                                                <string>WFTextTokenString</string>
                                            </dict>
                                            <key>WFValue</key>
                                            <dict>
                                                <key>Value</key>
                                                <dict>
                                                    <key>Type</key>
                                                    <string>Variable</string>
                                                    <key>VariableName</key>
                                                    <string>Repeat Item</string>
                                                </dict>
                                                <key>WFSerializationType</key>
                                                <string>WFTextTokenAttachment</string>
                                            </dict>
                                        </dict>
                                    </array>
                                </dict>
                                <key>WFSerializationType</key>
                                <string>WFDictionaryFieldValue</string>
                            </dict>
                        </dict>
                    </dict>
                    <dict>
                        <key>WFWorkflowActionIdentifier</key>
                        <string>{ActionRepeatEach}</string>
                        <key>WFWorkflowActionParameters</key>
                        <dict>
                            <key>GroupingIdentifier</key>
                            <string>{repeatGroupId}</string>
                            <key>WFControlFlowMode</key>
                            <integer>2</integer>
                        </dict>
                    </dict>
                    <dict>
                        <key>WFWorkflowActionIdentifier</key>
                        <string>{ActionNotification}</string>
                        <key>WFWorkflowActionParameters</key>
                        <dict>
                            <key>WFNotificationActionTitle</key>
                            <string>WinDrop 发送完成</string>
                            <key>WFNotificationActionBody</key>
                            <dict>
                                <key>Value</key>
                                <dict>
                                    <key>attachmentsByRange</key>
                                    <dict>
                                        <key>{RangeZeroOneKey}</key>
                                        <dict>
                                            <key>OutputName</key>
                                            <string>URL 的内容</string>
                                            <key>OutputUUID</key>
                                            <string>{httpActionUuid}</string>
                                            <key>Type</key>
                                            <string>ActionOutput</string>
                                        </dict>
                                    </dict>
                                    <key>string</key>
                                    <string>&#xFFFC;</string>
                                </dict>
                                <key>WFSerializationType</key>
                                <string>WFTextTokenString</string>
                            </dict>
                        </dict>
                    </dict>
                </array>
            </dict>
            </plist>
            """;

        return Encoding.UTF8.GetBytes(xml.Trim());
    }

    /// <summary>
    /// 生成「从电脑接收（零菜单直达 + 五路智能分类归档）」完整动作流 Plist
    /// 分支：url -> Safari 打开 | text -> 拷入剪贴板 | image / video -> 存入系统相册 | file -> 存入「文件」App
    /// </summary>
    public static byte[] BuildReceiveFromWindowsShortcutPlist(string host, int port, string key)
    {
        string receiveUrl = SecurityElement.Escape($"http://{host}:{port}/v2/receive")!;
        string escapedKey = SecurityElement.Escape(key)!;

        string getUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        string catUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        string textUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        string filesUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        string summaryUuid = Guid.NewGuid().ToString().ToUpperInvariant();

        string grpUrl = Guid.NewGuid().ToString().ToUpperInvariant();
        string grpText = Guid.NewGuid().ToString().ToUpperInvariant();
        string grpImage = Guid.NewGuid().ToString().ToUpperInvariant();
        string grpVideo = Guid.NewGuid().ToString().ToUpperInvariant();
        string grpFile = Guid.NewGuid().ToString().ToUpperInvariant();

        string loopImgGroup = Guid.NewGuid().ToString().ToUpperInvariant();
        string dlImgUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        string loopVidGroup = Guid.NewGuid().ToString().ToUpperInvariant();
        string dlVidUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        string loopFileGroup = Guid.NewGuid().ToString().ToUpperInvariant();
        string dlFileUuid = Guid.NewGuid().ToString().ToUpperInvariant();

        string xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>WFWorkflowClientVersion</key>
                <string>2302.0.4</string>
                <key>WFWorkflowMinimumClientVersion</key>
                <integer>900</integer>
                <key>WFWorkflowMinimumClientVersionString</key>
                <string>900</string>
                <key>WFWorkflowHasOutputFallback</key>
                <false/>
                <key>WFWorkflowHasShortcutInputVariables</key>
                <false/>
                <key>WFWorkflowImportQuestions</key>
                <array/>
                <key>WFWorkflowIcon</key>
                <dict>
                    <key>WFWorkflowIconStartColor</key>
                    <integer>4282601983</integer>
                    <key>WFWorkflowIconGlyphNumber</key>
                    <integer>59782</integer>
                </dict>
                <key>WFWorkflowTypes</key>
                <array/>
                <key>WFWorkflowInputContentItemClasses</key>
                <array/>
                <key>WFWorkflowOutputContentItemClasses</key>
                <array/>
                <key>WFWorkflowActions</key>
                <array>
                    <dict>
                        <key>WFWorkflowActionIdentifier</key>
                        <string>{ActionGetContentsOfUrl}</string>
                        <key>WFWorkflowActionParameters</key>
                        <dict>
                            <key>UUID</key>
                            <string>{getUuid}</string>
                            <key>Advanced</key>
                            <true/>
                            <key>WFHTTPMethod</key>
                            <string>GET</string>
                            <key>WFURL</key>
                            <string>{receiveUrl}</string>
                            <key>WFHTTPHeaders</key>
                            <dict>
                                <key>Value</key>
                                <dict>
                                    <key>WFDictionaryFieldValueItems</key>
                                    <array>
                                        {BuildDictTextItem("Authorization", escapedKey)}
                                    </array>
                                </dict>
                                <key>WFSerializationType</key>
                                <string>WFDictionaryFieldValue</string>
                            </dict>
                        </dict>
                    </dict>
                    {BuildGetDictValueAction(getUuid, "category", catUuid)}
                    {BuildGetDictValueAction(getUuid, "text", textUuid)}
                    {BuildGetDictValueAction(getUuid, "files", filesUuid)}
                    {BuildGetDictValueAction(getUuid, "summary", summaryUuid)}

                    {BuildIfEqualsStart(catUuid, "url", grpUrl)}
                    <dict>
                        <key>WFWorkflowActionIdentifier</key>
                        <string>{ActionOpenUrl}</string>
                        <key>WFWorkflowActionParameters</key>
                        <dict>
                            <key>WFInput</key>
                            <dict>
                                <key>Value</key>
                                <dict>
                                    <key>OutputUUID</key>
                                    <string>{textUuid}</string>
                                    <key>Type</key>
                                    <string>ActionOutput</string>
                                </dict>
                                <key>WFSerializationType</key>
                                <string>WFTextTokenAttachment</string>
                            </dict>
                        </dict>
                    </dict>
                    {BuildIfEnd(grpUrl)}

                    {BuildIfEqualsStart(catUuid, "text", grpText)}
                    <dict>
                        <key>WFWorkflowActionIdentifier</key>
                        <string>{ActionSetClipboard}</string>
                        <key>WFWorkflowActionParameters</key>
                        <dict>
                            <key>WFInput</key>
                            <dict>
                                <key>Value</key>
                                <dict>
                                    <key>OutputUUID</key>
                                    <string>{textUuid}</string>
                                    <key>Type</key>
                                    <string>ActionOutput</string>
                                </dict>
                                <key>WFSerializationType</key>
                                <string>WFTextTokenAttachment</string>
                            </dict>
                        </dict>
                    </dict>
                    {BuildSimpleNotification("已复制到剪贴板", summaryUuid)}
                    {BuildIfEnd(grpText)}

                    {BuildIfEqualsStart(catUuid, "image", grpImage)}
                    {BuildRepeatDownloadAndSave(filesUuid, loopImgGroup, dlImgUuid, ActionSaveToPhotoAlbum)}
                    {BuildSimpleNotification("图片已存入系统相册", summaryUuid)}
                    {BuildIfEnd(grpImage)}

                    {BuildIfEqualsStart(catUuid, "video", grpVideo)}
                    {BuildRepeatDownloadAndSave(filesUuid, loopVidGroup, dlVidUuid, ActionSaveToPhotoAlbum)}
                    {BuildSimpleNotification("视频已存入系统相册", summaryUuid)}
                    {BuildIfEnd(grpVideo)}

                    {BuildIfEqualsStart(catUuid, "file", grpFile)}
                    {BuildRepeatDownloadAndSave(filesUuid, loopFileGroup, dlFileUuid, ActionSaveFile)}
                    {BuildSimpleNotification("文件已保存到 iPhone", summaryUuid)}
                    {BuildIfEnd(grpFile)}
                </array>
            </dict>
            </plist>
            """;

        return Encoding.UTF8.GetBytes(xml.Trim());
    }

    /// <summary>
    /// 生成 macOS 本地官方签名脚本（调用 Apple 官方 /usr/bin/shortcuts sign --mode anyone）
    /// </summary>
    public static string BuildMacSignScript()
    {
        return """
            #!/usr/bin/env bash
            set -euo pipefail

            echo "正在转换二进制 Plist 并调用 macOS 系统原生 shortcuts 工具进行 Apple 签名..."
            plutil -convert binary1 "$PWD/发送到电脑.shortcut" -o "$PWD/send_bin.shortcut"
            plutil -convert binary1 "$PWD/从电脑接收.shortcut" -o "$PWD/recv_bin.shortcut"
            shortcuts sign --mode anyone --input "$PWD/send_bin.shortcut" --output "$PWD/发送到电脑_已签名.shortcut"
            shortcuts sign --mode anyone --input "$PWD/recv_bin.shortcut" --output "$PWD/从电脑接收_已签名.shortcut"
            rm -f "$PWD/send_bin.shortcut" "$PWD/recv_bin.shortcut"

            echo "签名完成！请将 *_已签名.shortcut 通过隔空投送或 iCloud 链接直接导入任意 iPhone。"
            """;
    }

    private static string BuildDictTextItem(string keyName, string valueText) => $"""
        <dict>
            <key>WFItemType</key>
            <integer>0</integer>
            <key>WFKey</key>
            <dict>
                <key>Value</key>
                <dict>
                    <key>attachmentsByRange</key>
                    <dict/>
                    <key>string</key>
                    <string>{keyName}</string>
                </dict>
                <key>WFSerializationType</key>
                <string>WFTextTokenString</string>
            </dict>
            <key>WFValue</key>
            <dict>
                <key>Value</key>
                <dict>
                    <key>attachmentsByRange</key>
                    <dict/>
                    <key>string</key>
                    <string>{valueText}</string>
                </dict>
                <key>WFSerializationType</key>
                <string>WFTextTokenString</string>
            </dict>
        </dict>
        """;

    private static string BuildGetDictValueAction(string dictOutputUuid, string keyName, string outputUuid) => $"""
        <dict>
            <key>WFWorkflowActionIdentifier</key>
            <string>{ActionGetDictionaryValue}</string>
            <key>WFWorkflowActionParameters</key>
            <dict>
                <key>UUID</key>
                <string>{outputUuid}</string>
                <key>WFDictionaryKey</key>
                <string>{keyName}</string>
                <key>WFInput</key>
                <dict>
                    <key>Value</key>
                    <dict>
                        <key>OutputUUID</key>
                        <string>{dictOutputUuid}</string>
                        <key>Type</key>
                        <string>ActionOutput</string>
                    </dict>
                    <key>WFSerializationType</key>
                    <string>WFTextTokenAttachment</string>
                </dict>
            </dict>
        </dict>
        """;

    private static string BuildIfEqualsStart(string inputOutputUuid, string expectedValue, string groupingId) => $"""
        <dict>
            <key>WFWorkflowActionIdentifier</key>
            <string>{ActionConditional}</string>
            <key>WFWorkflowActionParameters</key>
            <dict>
                <key>GroupingIdentifier</key>
                <string>{groupingId}</string>
                <key>WFCondition</key>
                <integer>4</integer>
                <key>WFConditionalActionString</key>
                <string>{expectedValue}</string>
                <key>WFControlFlowMode</key>
                <integer>0</integer>
                <key>WFInput</key>
                <dict>
                    <key>Type</key>
                    <string>Variable</string>
                    <key>Variable</key>
                    <dict>
                        <key>Value</key>
                        <dict>
                            <key>OutputUUID</key>
                            <string>{inputOutputUuid}</string>
                            <key>Type</key>
                            <string>ActionOutput</string>
                        </dict>
                        <key>WFSerializationType</key>
                        <string>WFTextTokenAttachment</string>
                    </dict>
                </dict>
            </dict>
        </dict>
        """;

    private static string BuildIfEnd(string groupingId) => $"""
        <dict>
            <key>WFWorkflowActionIdentifier</key>
            <string>{ActionConditional}</string>
            <key>WFWorkflowActionParameters</key>
            <dict>
                <key>GroupingIdentifier</key>
                <string>{groupingId}</string>
                <key>WFControlFlowMode</key>
                <integer>2</integer>
            </dict>
        </dict>
        """;

    private static string BuildRepeatDownloadAndSave(string filesOutputUuid, string repeatGroupId, string downloadOutputUuid, string saveActionId)
    {
        string itemUrlUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        return $"""
            <dict>
                <key>WFWorkflowActionIdentifier</key>
                <string>{ActionRepeatEach}</string>
                <key>WFWorkflowActionParameters</key>
                <dict>
                    <key>GroupingIdentifier</key>
                    <string>{repeatGroupId}</string>
                    <key>WFControlFlowMode</key>
                    <integer>0</integer>
                    <key>WFInput</key>
                    <dict>
                        <key>Value</key>
                        <dict>
                            <key>OutputUUID</key>
                            <string>{filesOutputUuid}</string>
                            <key>Type</key>
                            <string>ActionOutput</string>
                        </dict>
                        <key>WFSerializationType</key>
                        <string>WFTextTokenAttachment</string>
                    </dict>
                </dict>
            </dict>
            <dict>
                <key>WFWorkflowActionIdentifier</key>
                <string>{ActionGetDictionaryValue}</string>
                <key>WFWorkflowActionParameters</key>
                <dict>
                    <key>UUID</key>
                    <string>{itemUrlUuid}</string>
                    <key>WFDictionaryKey</key>
                    <string>url</string>
                    <key>WFInput</key>
                    <dict>
                        <key>Value</key>
                        <dict>
                            <key>Type</key>
                            <string>Variable</string>
                            <key>VariableName</key>
                            <string>Repeat Item</string>
                        </dict>
                        <key>WFSerializationType</key>
                        <string>WFTextTokenAttachment</string>
                    </dict>
                </dict>
            </dict>
            <dict>
                <key>WFWorkflowActionIdentifier</key>
                <string>{ActionGetContentsOfUrl}</string>
                <key>WFWorkflowActionParameters</key>
                <dict>
                    <key>UUID</key>
                    <string>{downloadOutputUuid}</string>
                    <key>WFHTTPMethod</key>
                    <string>GET</string>
                    <key>WFURL</key>
                    <dict>
                        <key>Value</key>
                        <dict>
                            <key>attachmentsByRange</key>
                            <dict>
                                <key>{RangeZeroOneKey}</key>
                                <dict>
                                    <key>OutputUUID</key>
                                    <string>{itemUrlUuid}</string>
                                    <key>Type</key>
                                    <string>ActionOutput</string>
                                </dict>
                            </dict>
                            <key>string</key>
                            <string>&#xFFFC;</string>
                        </dict>
                        <key>WFSerializationType</key>
                        <string>WFTextTokenString</string>
                    </dict>
                </dict>
            </dict>
            <dict>
                <key>WFWorkflowActionIdentifier</key>
                <string>{saveActionId}</string>
                <key>WFWorkflowActionParameters</key>
                <dict>
                    <key>WFInput</key>
                    <dict>
                        <key>Value</key>
                        <dict>
                            <key>OutputUUID</key>
                            <string>{downloadOutputUuid}</string>
                            <key>Type</key>
                            <string>ActionOutput</string>
                        </dict>
                        <key>WFSerializationType</key>
                        <string>WFTextTokenAttachment</string>
                    </dict>
                </dict>
            </dict>
            <dict>
                <key>WFWorkflowActionIdentifier</key>
                <string>{ActionRepeatEach}</string>
                <key>WFWorkflowActionParameters</key>
                <dict>
                    <key>GroupingIdentifier</key>
                    <string>{repeatGroupId}</string>
                    <key>WFControlFlowMode</key>
                    <integer>2</integer>
                </dict>
            </dict>
            """;
    }

    private static string BuildSimpleNotification(string title, string bodyOutputUuid) => $"""
        <dict>
            <key>WFWorkflowActionIdentifier</key>
            <string>{ActionNotification}</string>
            <key>WFWorkflowActionParameters</key>
            <dict>
                <key>WFNotificationActionTitle</key>
                <string>{title}</string>
                <key>WFNotificationActionBody</key>
                <dict>
                    <key>Value</key>
                    <dict>
                        <key>attachmentsByRange</key>
                        <dict>
                            <key>{RangeZeroOneKey}</key>
                            <dict>
                                <key>OutputUUID</key>
                                <string>{bodyOutputUuid}</string>
                                <key>Type</key>
                                <string>ActionOutput</string>
                            </dict>
                        </dict>
                        <key>string</key>
                        <string>&#xFFFC;</string>
                    </dict>
                    <key>WFSerializationType</key>
                    <string>WFTextTokenString</string>
                </dict>
            </dict>
        </dict>
        """;
}
