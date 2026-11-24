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
    private const string ActionCount = "is.workflow.actions.count";
    private const string RangeZeroOneKey = "{0, 1}";
    private const string RangeTenOneKey = "{10, 1}";

    /// <summary>
    /// 生成「发送到电脑（共享表单自动发）」完整动作流 Plist
    /// 动作流：统计共享表单数量 -> 循环逐项 POST /v2/send (Multipart Form: file) -> 显示结果通知
    /// </summary>
    public static byte[] BuildSendToWindowsShortcutPlist(string host, int port, string key)
    {
        string sendUrl = SecurityElement.Escape($"http://{host}:{port}/v2/send")!;
        string escapedKey = SecurityElement.Escape(key)!;
        string countUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        string repeatGroupId = Guid.NewGuid().ToString().ToUpperInvariant();
        string httpActionUuid = Guid.NewGuid().ToString().ToUpperInvariant();

        string xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>WFWorkflowClientVersion</key>
                <string>3107.0.10</string>
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
                <key>WFQuickActionSurfaces</key>
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
                        <string>{ActionCount}</string>
                        <key>WFWorkflowActionParameters</key>
                        <dict>
                            <key>UUID</key>
                            <string>{countUuid}</string>
                            <key>WFCountType</key>
                            <string>Items</string>
                            <key>Input</key>
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
                            <key>ShowHeaders</key>
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
                                                <dict><key>string</key><string>file</string></dict>
                                                <key>WFSerializationType</key>
                                                <string>WFTextTokenString</string>
                                            </dict>
                                            <key>WFValue</key>
                                            <dict>
                                                <key>Value</key>
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
                                                <key>WFSerializationType</key>
                                                <string>WFTokenAttachmentParameterState</string>
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
                                        <key>{RangeTenOneKey}</key>
                                        <dict>
                                            <key>OutputName</key>
                                            <string>计数</string>
                                            <key>OutputUUID</key>
                                            <string>{countUuid}</string>
                                            <key>Type</key>
                                            <string>ActionOutput</string>
                                        </dict>
                                    </dict>
                                    <key>string</key>
                                    <string>已全部发送到电脑（共&#xFFFC;项）</string>
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
    /// 生成「从电脑接收（极简 14 核心积木单循环 + 全自动归档）」完整动作流 Plist
    /// 分支：action=url -> 打开网址 | action=text -> 拷入剪贴板 | urls 单循环下载后按 action=media/file 存相册或「文件」App
    /// </summary>
    public static byte[] BuildReceiveFromWindowsShortcutPlist(string host, int port, string key)
    {
        string receiveUrl = SecurityElement.Escape($"http://{host}:{port}/v2/receive")!;
        string escapedKey = SecurityElement.Escape(key)!;

        string getUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        string actionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        string urlTextUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        string clipTextUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        string urlsUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        string summaryUuid = Guid.NewGuid().ToString().ToUpperInvariant();

        string grpUrl = Guid.NewGuid().ToString().ToUpperInvariant();
        string grpText = Guid.NewGuid().ToString().ToUpperInvariant();
        string grpMedia = Guid.NewGuid().ToString().ToUpperInvariant();
        string loopGroupId = Guid.NewGuid().ToString().ToUpperInvariant();
        string dlUuid = Guid.NewGuid().ToString().ToUpperInvariant();

        string xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>WFWorkflowClientVersion</key>
                <string>3107.0.10</string>
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
                <key>WFQuickActionSurfaces</key>
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
                            <key>ShowHeaders</key>
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
                    {BuildGetDictValueAction(getUuid, "action", actionUuid)}

                    {BuildIfEqualsStart(actionUuid, "url", grpUrl)}
                    {BuildGetDictValueAction(getUuid, "text", urlTextUuid)}
                    {BuildActionWithOutputInput(ActionOpenUrl, urlTextUuid, "词典值")}
                    {BuildIfEnd(grpUrl)}

                    {BuildIfEqualsStart(actionUuid, "text", grpText)}
                    {BuildGetDictValueAction(getUuid, "text", clipTextUuid)}
                    {BuildActionWithOutputInput(ActionSetClipboard, clipTextUuid, "词典值")}
                    {BuildIfEnd(grpText)}

                    {BuildGetDictValueAction(getUuid, "urls", urlsUuid)}
                    <dict>
                        <key>WFWorkflowActionIdentifier</key>
                        <string>{ActionRepeatEach}</string>
                        <key>WFWorkflowActionParameters</key>
                        <dict>
                            <key>GroupingIdentifier</key>
                            <string>{loopGroupId}</string>
                            <key>WFControlFlowMode</key>
                            <integer>0</integer>
                            <key>WFInput</key>
                            <dict>
                                <key>Value</key>
                                <dict>
                                    <key>OutputName</key>
                                    <string>词典值</string>
                                    <key>OutputUUID</key>
                                    <string>{urlsUuid}</string>
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
                        <string>{ActionGetContentsOfUrl}</string>
                        <key>WFWorkflowActionParameters</key>
                        <dict>
                            <key>UUID</key>
                            <string>{dlUuid}</string>
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
                                            <key>Type</key>
                                            <string>Variable</string>
                                            <key>VariableName</key>
                                            <string>Repeat Item</string>
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
                    {BuildIfEqualsStart(actionUuid, "media", grpMedia)}
                    {BuildActionWithOutputInput(ActionSaveToPhotoAlbum, dlUuid, "URL 的内容")}
                    {BuildIfElse(grpMedia)}
                    {BuildActionWithOutputInput(ActionSaveFile, dlUuid, "URL 的内容")}
                    {BuildIfEnd(grpMedia)}
                    <dict>
                        <key>WFWorkflowActionIdentifier</key>
                        <string>{ActionRepeatEach}</string>
                        <key>WFWorkflowActionParameters</key>
                        <dict>
                            <key>GroupingIdentifier</key>
                            <string>{loopGroupId}</string>
                            <key>WFControlFlowMode</key>
                            <integer>2</integer>
                        </dict>
                    </dict>

                    {BuildGetDictValueAction(getUuid, "summary", summaryUuid)}
                    {BuildSimpleNotification("WinDrop 接收完成", summaryUuid)}
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
                <dict><key>string</key><string>{keyName}</string></dict>
                <key>WFSerializationType</key>
                <string>WFTextTokenString</string>
            </dict>
            <key>WFValue</key>
            <dict>
                <key>Value</key>
                <dict><key>string</key><string>{valueText}</string></dict>
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
                        <key>OutputName</key>
                        <string>URL 的内容</string>
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
                            <key>Aggrandizements</key>
                            <array>
                                <dict>
                                    <key>CoercionItemClass</key>
                                    <string>WFStringContentItem</string>
                                    <key>Type</key>
                                    <string>WFCoercionVariableAggrandizement</string>
                                </dict>
                            </array>
                            <key>OutputName</key>
                            <string>词典值</string>
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

    private static string BuildIfElse(string groupingId) => $"""
        <dict>
            <key>WFWorkflowActionIdentifier</key>
            <string>{ActionConditional}</string>
            <key>WFWorkflowActionParameters</key>
            <dict>
                <key>GroupingIdentifier</key>
                <string>{groupingId}</string>
                <key>WFControlFlowMode</key>
                <integer>1</integer>
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

    private static string BuildActionWithOutputInput(string actionId, string inputOutputUuid, string outputName)
    {
        string extraParam = actionId switch
        {
            ActionSaveToPhotoAlbum => """
                <key>WFCameraRollSelectedGroup</key>
                <string>Recents</string>
""",
            ActionSaveFile => """
                <key>WFAskWhereToSave</key>
                <false/>
""",
            _ => string.Empty
        };

        return $"""
            <dict>
                <key>WFWorkflowActionIdentifier</key>
                <string>{actionId}</string>
                <key>WFWorkflowActionParameters</key>
                <dict>
            {extraParam}        <key>WFInput</key>
                    <dict>
                        <key>Value</key>
                        <dict>
                            <key>OutputName</key>
                            <string>{outputName}</string>
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
                                <key>OutputName</key>
                                <string>词典值</string>
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
