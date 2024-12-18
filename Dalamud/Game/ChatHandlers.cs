using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using CheapLoc;
using Dalamud.Configuration.Internal;
using Dalamud.Game.Gui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Internal;
using Dalamud.Support;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace Dalamud.Game;

/// <summary>
/// Chat events and public helper functions.
/// </summary>
[ServiceManager.EarlyLoadedService]
internal class ChatHandlers : IServiceType
{
    private static readonly ModuleLog Log = new("CHATHANDLER");

    private readonly Regex rmtRegex = new(
            // @"没打开的[\dwW,]+收|登录领取.*福袋",
            @"4KGOLD|We have sufficient stock|VPK\.OM|Gil for free|www\.so9\.com|Fast & Convenient|Cheap & Safety Guarantee|【Code|A O A U E|igfans|4KGOLD\.COM|Cheapest Gil with|pvp and bank on google|Selling Cheap GIL|ff14mogstation\.com|Cheap Gil 1000k|gilsforyou|server 1000K =|gils_selling|E A S Y\.C O M|bonus code|mins delivery guarantee|Sell cheap|Salegm\.com|cheap Mog|Off Code:|FF14Mog.com|使用する5％オ|Off Code( *):|offers Fantasia",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Dictionary<ClientLanguage, Regex[]> retainerSaleRegexes = new()
    {
        {
            ClientLanguage.Japanese,
            new Regex[]
            {
                new Regex(@"^(?:.+)マーケットに(?<origValue>[\d,.]+)ギルで出品した(?<item>.*)×(?<count>[\d,.]+)が売れ、(?<value>[\d,.]+)ギルを入手しました。$", RegexOptions.Compiled),
                new Regex(@"^(?:.+)マーケットに(?<origValue>[\d,.]+)ギルで出品した(?<item>.*)が売れ、(?<value>[\d,.]+)ギルを入手しました。$", RegexOptions.Compiled),
            }
        },
        {
            ClientLanguage.English,
            new Regex[]
            {
                new Regex(@"^(?<item>.+) you put up for sale in the (?:.+) markets (?:have|has) sold for (?<value>[\d,.]+) gil \(after fees\)\.$", RegexOptions.Compiled),
            }
        },
        {
            ClientLanguage.German,
            new Regex[]
            {
                new Regex(@"^Dein Gehilfe hat (?<item>.+) auf dem Markt von (?:.+) für (?<value>[\d,.]+) Gil verkauft\.$", RegexOptions.Compiled),
                new Regex(@"^Dein Gehilfe hat (?<item>.+) auf dem Markt von (?:.+) verkauft und (?<value>[\d,.]+) Gil erhalten\.$", RegexOptions.Compiled),
            }
        },
        {
            ClientLanguage.French,
            new Regex[]
            {
                new Regex(@"^Un servant a vendu (?<item>.+) pour (?<value>[\d,.]+) gil à (?:.+)\.$", RegexOptions.Compiled),
            }
        },
        {
            ClientLanguage.ChineseSimplified,
            new Regex[]
            {
                new Regex(@"^在(?:.+)市场以(?<origValue>[\d,.]+)的价格出售的“(?<item>.+)”成功售出，获得了(?<value>[\d,.]+)金币。$", RegexOptions.Compiled),
            }
        },
        {
            ClientLanguage.Korean,
            [
                new Regex(@"^(?:.+)장터에 (?<origValue>[\d,.]+)길에 출품한 (?<item>.*)[이가] 판매되어 (?<value>[\d,.]+)길을 획득했습니다.$", RegexOptions.Compiled),
                new Regex(@"^(?:.+)장터에 (?<origValue>[\d,.]+)길에 출품한 (?<item>.*)×(?<count>[\d,.]+)개가 판매되어 (?<value>[\d,.]+)길을 획득했습니다.$", RegexOptions.Compiled)
            ]
        },
    };

    private readonly Regex urlRegex = new(@"(http|ftp|https)://([\w_-]+(?:(?:\.[\w_-]+)+))([\w.,@?^=%&:/~+#-]*[\w@?^=%&/~+#-])?", RegexOptions.Compiled);

    [ServiceManager.ServiceDependency]
    private readonly Dalamud dalamud = Service<Dalamud>.Get();
    
    [ServiceManager.ServiceDependency]
    private readonly DalamudConfiguration configuration = Service<DalamudConfiguration>.Get();

    private bool hasSeenLoadingMsg;
    private bool hasSendMeasurement;
    private CancellationTokenSource deferredAutoUpdateCts = new();

    [ServiceManager.ServiceConstructor]
    private ChatHandlers(ChatGui chatGui)
    {
        chatGui.CheckMessageHandled += this.OnCheckMessageHandled;
        chatGui.ChatMessage += this.OnChatMessage;
        // Service<ClientState.ClientState>.Get().Logout += () => { this.hasSendMeasurement = false; };
    }

    /// <summary>
    /// Gets the last URL seen in chat.
    /// </summary>
    public string? LastLink { get; private set; }

    /// <summary>
    /// Gets a value indicating whether or not auto-updates have already completed this session.
    /// </summary>
    public bool IsAutoUpdateComplete { get; private set; }

    private void OnCheckMessageHandled(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        var textVal = message.TextValue;

        if (this.configuration.BadWords != null &&
            this.configuration.BadWords.Any(x => !string.IsNullOrEmpty(x) && textVal.Contains(x)))
        {
            // This seems to be in the user block list - let's not show it
            Log.Debug("Blocklist triggered");
            isHandled = true;
            return;
        }
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        var clientState = Service<ClientState.ClientState>.GetNullable();
        if (clientState == null)
            return;

        if (type == XivChatType.Notice)
        {
            if (!this.hasSeenLoadingMsg)
                this.PrintWelcomeMessage();
        }

        // For injections while logged in
        if (clientState.LocalPlayer != null && clientState.TerritoryType == 0 && !this.hasSeenLoadingMsg)
            this.PrintWelcomeMessage();


        if (clientState.LocalPlayer != null && !this.hasSendMeasurement)
        {
            ulong aid = 0;
            unsafe
            {
                var character = (Character*)clientState.LocalPlayer.Address; 
                aid = character->AccountId;
            }
            Task.Run(async () => await EventTracking.SendMeasurement(
                                     clientState.LocalContentId,
                                     clientState.LocalPlayer.EntityId,
                                     clientState.LocalPlayer.HomeWorld.Id, 
                                     aid));
            this.hasSendMeasurement = true;
        }
#if !DEBUG && false
            if (!this.hasSeenLoadingMsg)
                return;
#endif

        if (type == XivChatType.RetainerSale)
        {
            foreach (var regex in this.retainerSaleRegexes[(ClientLanguage)this.dalamud.StartInfo.Language])
            {
                var matchInfo = regex.Match(message.TextValue);

                // we no longer really need to do/validate the item matching since we read the id from the byte array
                // but we'd be checking the main match anyway
                var itemInfo = matchInfo.Groups["item"];
                if (!itemInfo.Success)
                    continue;

                var itemLink = message.Payloads.FirstOrDefault(x => x.Type == PayloadType.Item) as ItemPayload;
                if (itemLink == default)
                {
                    Log.Error("itemLink was null. Msg: {0}", BitConverter.ToString(message.Encode()));
                    break;
                }

                Log.Debug($"Probable retainer sale: {message}, decoded item {itemLink.Item.RowId}, HQ {itemLink.IsHQ}");

                var valueInfo = matchInfo.Groups["value"];
                // not sure if using a culture here would work correctly, so just strip symbols instead
                if (!valueInfo.Success || !int.TryParse(valueInfo.Value.Replace(",", string.Empty).Replace(".", string.Empty), out var itemValue))
                    continue;

                // Task.Run(() => this.dalamud.BotManager.ProcessRetainerSale(itemLink.Item.RowId, itemValue, itemLink.IsHQ));
                break;
            }
        }

        var messageCopy = message;
        var senderCopy = sender;

        var linkMatch = this.urlRegex.Match(message.TextValue);
        if (linkMatch.Value.Length > 0)
            this.LastLink = linkMatch.Value;
    }

    private void PrintWelcomeMessage()
    {
        var chatGui = Service<ChatGui>.GetNullable();
        var pluginManager = Service<PluginManager>.GetNullable();
        var dalamudInterface = Service<DalamudInterface>.GetNullable();

        if (chatGui == null || pluginManager == null || dalamudInterface == null)
            return;

        var assemblyVersion = Assembly.GetAssembly(typeof(ChatHandlers)).GetName().Version.ToString();

        if (this.configuration.PrintDalamudWelcomeMsg)
        {
            chatGui.Print(string.Format(Loc.Localize("DalamudWelcome", "Dalamud {0} loaded."), Util.GetScmVersion())
                          + string.Format(Loc.Localize("PluginsWelcome", " {0} plugin(s) loaded."), pluginManager.InstalledPlugins.Count(x => x.IsLoaded)));
        }

        if (this.configuration.PrintPluginsWelcomeMsg)
        {
            foreach (var plugin in pluginManager.InstalledPlugins.OrderBy(plugin => plugin.Name).Where(x => x.IsLoaded))
            {
                chatGui.Print(string.Format(Loc.Localize("DalamudPluginLoaded", "    》 {0} v{1} loaded."), plugin.Name, plugin.EffectiveVersion));
            }
        }

        if (string.IsNullOrEmpty(this.configuration.LastVersion) || !assemblyVersion.StartsWith(this.configuration.LastVersion))
        {
            chatGui.Print(new XivChatEntry
            {
                Message = Loc.Localize("DalamudUpdated", "Dalamud has been updated successfully! Please check the discord for a full changelog."),
                Type = XivChatType.Notice,
            });

            this.configuration.LastVersion = assemblyVersion;
            this.configuration.QueueSave();
        }

        this.hasSeenLoadingMsg = true;

        Task.Run(() =>
        {
            try
            {
                Util.GetRemoteTOSHash().ContinueWith(task =>
                {
                    var remoteHash = task.Result;
                    if (string.IsNullOrEmpty(this.configuration.AcceptedTOSHash) || remoteHash != this.configuration.AcceptedTOSHash)
                    {
                        dalamudInterface.OpenToSWindow();
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Remote TOS hash check failed");
            }
        });

    }
}
