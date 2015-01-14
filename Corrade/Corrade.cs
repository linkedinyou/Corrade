﻿///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2013 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

#region

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration.Install;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using Mono.Unix;
using Mono.Unix.Native;
using OpenMetaverse;
using OpenMetaverse.Assets;
using ThreadState = System.Threading.ThreadState;

#endregion

namespace Corrade
{
    public partial class Corrade : ServiceBase
    {
        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2015 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////

        public delegate bool EventHandler(NativeMethods.CtrlType ctrlType);

        /// <summary>
        ///     Possible actions.
        /// </summary>
        public enum Action : uint
        {
            [Description("none")] NONE = 0,
            [Description("get")] GET,
            [Description("set")] SET,
            [Description("add")] ADD,
            [Description("remove")] REMOVE,
            [Description("start")] START,
            [Description("stop")] STOP,
            [Description("mute")] MUTE,
            [Description("unmute")] UNMUTE,
            [Description("restart")] RESTART,
            [Description("cancel")] CANCEL,
            [Description("accept")] ACCEPT,
            [Description("decline")] DECLINE,
            [Description("online")] ONLINE,
            [Description("offline")] OFFLINE,
            [Description("request")] REQUEST,
            [Description("response")] RESPONSE,
            [Description("delete")] DELETE,
            [Description("take")] TAKE,
            [Description("read")] READ,
            [Description("wrtie")] WRITE,
            [Description("purge")] PURGE,
            [Description("crossed")] CROSSED,
            [Description("changed")] CHANGED,
            [Description("reply")] REPLY,
            [Description("offer")] OFFER,
            [Description("generic")] GENERIC,
            [Description("point")] POINT,
            [Description("look")] LOOK,
            [Description("update")] UPDATE,
            [Description("received")] RECEIVED,
            [Description("joined")] JOINED,
            [Description("parted")] PARTED,
            [Description("save")] SAVE,
            [Description("load")] LOAD,
            [Description("enable")] ENABLE,
            [Description("disable")] DISABLE
        }

        /// <summary>
        ///     Corrade version sent to the simulator.
        /// </summary>
        private static readonly string CORRADE_VERSION = Assembly.GetEntryAssembly().GetName().Version.ToString();

        /// <summary>
        ///     Corrade compile date.
        /// </summary>
        private static readonly string CORRADE_COMPILE_DATE = new DateTime(2000, 1, 1).Add(new TimeSpan(
            TimeSpan.TicksPerDay*Assembly.GetEntryAssembly().GetName().Version.Build + // days since 1 January 2000
            TimeSpan.TicksPerSecond*2*Assembly.GetEntryAssembly().GetName().Version.Revision)).ToLongDateString();

        /// <summary>
        ///     Semaphores that sense the state of the connection. When any of these semaphores fail,
        ///     Corrade does not consider itself connected anymore and terminates.
        /// </summary>
        private static readonly Dictionary<char, ManualResetEvent> ConnectionSemaphores = new Dictionary
            <char, ManualResetEvent>
        {
            {'l', new ManualResetEvent(false)},
            {'s', new ManualResetEvent(false)},
            {'u', new ManualResetEvent(false)}
        };

        public static string CorradeServiceName;

        private static Thread programThread;

        private static readonly EventLog CorradeLog = new EventLog();

        private static readonly GridClient Client = new GridClient();

        private static readonly object ServicesLock = new object();

        private static readonly object InventoryLock = new object();

        private static readonly object ConfigurationFileLock = new object();

        private static readonly object LogFileLock = new object();

        private static readonly object DatabaseFileLock = new object();

        private static readonly Dictionary<string, object> DatabaseLocks = new Dictionary<string, object>();

        private static readonly object GroupNotificationsLock = new object();

        private static readonly HashSet<Notification> GroupNotifications = new HashSet<Notification>();

        private static readonly object TeleportLock = new object();

        private static readonly Dictionary<InventoryObjectOfferedEventArgs, ManualResetEvent> InventoryOffers =
            new Dictionary<InventoryObjectOfferedEventArgs, ManualResetEvent>();

        private static readonly object InventoryOffersLock = new object();

        private static readonly Queue<CallbackQueueElement> CallbackQueue = new Queue<CallbackQueueElement>();

        private static readonly object CallbackQueueLock = new object();

        private static readonly Queue<NotificationQueueElement> NotificationQueue =
            new Queue<NotificationQueueElement>();

        private static readonly object NotificationQueueLock = new object();

        private static readonly HashSet<GroupInvite> GroupInvites = new HashSet<GroupInvite>();

        private static readonly object GroupInviteLock = new object();

        private static readonly HashSet<TeleportLure> TeleportLures = new HashSet<TeleportLure>();

        private static readonly object TeleportLureLock = new object();

        private static readonly HashSet<ScriptPermissionRequest> ScriptPermissionRequests =
            new HashSet<ScriptPermissionRequest>();

        private static readonly object ScriptPermissionRequestLock = new object();

        private static readonly HashSet<ScriptDialog> ScriptDialogs = new HashSet<ScriptDialog>();

        private static readonly object ScriptDialogLock = new object();

        private static readonly Dictionary<UUID, HashSet<UUID>> GroupMembers =
            new Dictionary<UUID, HashSet<UUID>>();

        private static readonly object GroupMembersLock = new object();

        private static volatile bool EnableCorradeRLV = true;

        public static EventHandler ConsoleEventHandler;

        private static readonly System.Action LoadInventoryCache = () =>
        {
            int itemsLoaded;
            lock (InventoryLock)
            {
                itemsLoaded =
                    Client.Inventory.Store.RestoreFromDisk(Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY,
                        CORRADE_CONSTANTS.INVENTORY_CACHE_FILE));
            }
            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.INVENTORY_CACHE_ITEMS_LOADED),
                itemsLoaded < 0 ? "0" : itemsLoaded.ToString(CultureInfo.InvariantCulture));
        };

        private static readonly System.Action InventoryUpdate = () =>
        {
            // Create the queue of folders.
            Queue<InventoryFolder> inventoryFolders = new Queue<InventoryFolder>();
            // Enqueue the first folder (root).
            inventoryFolders.Enqueue(Client.Inventory.Store.RootFolder);

            // Create a list of semaphores indexed by the folder UUID.
            Dictionary<UUID, AutoResetEvent> FolderUpdatedEvents = new Dictionary<UUID, AutoResetEvent>();
            object FolderUpdatedEventsLock = new object();
            EventHandler<FolderUpdatedEventArgs> FolderUpdatedEventHandler = (sender, args) =>
            {
                // Enqueue all the new folders.
                Client.Inventory.Store.GetContents(args.FolderID).ForEach(o =>
                {
                    if (o is InventoryFolder)
                    {
                        inventoryFolders.Enqueue(o as InventoryFolder);
                    }
                });
                FolderUpdatedEvents[args.FolderID].Set();
            };

            do
            {
                // Dequeue all the folders in the queue (can also limit to a number of folders).
                HashSet<InventoryFolder> folders = new HashSet<InventoryFolder>();
                do
                {
                    folders.Add(inventoryFolders.Dequeue());
                } while (!inventoryFolders.Count.Equals(0));
                // Process all the dequeued elements in parallel.
                Parallel.ForEach(folders.Where(o => o != null), o =>
                {
                    // Add an semaphore to wait for the folder contents.
                    lock (FolderUpdatedEventsLock)
                    {
                        FolderUpdatedEvents.Add(o.UUID, new AutoResetEvent(false));
                    }
                    Client.Inventory.FolderUpdated += FolderUpdatedEventHandler;
                    Client.Inventory.RequestFolderContents(o.UUID, Client.Self.AgentID, true, true,
                        InventorySortOrder.ByDate);
                    // Wait on the semaphore.
                    FolderUpdatedEvents[o.UUID].WaitOne(Configuration.SERVICES_TIMEOUT, false);
                    Client.Inventory.FolderUpdated -= FolderUpdatedEventHandler;
                    // Remove the semaphore for the folder.
                    lock (FolderUpdatedEventsLock)
                    {
                        FolderUpdatedEvents.Remove(o.UUID);
                    }
                });
            } while (!inventoryFolders.Count.Equals(0));
        };

        private static readonly System.Action SaveInventoryCache = () =>
        {
            lock (InventoryLock)
            {
                string path = Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY,
                    CORRADE_CONSTANTS.INVENTORY_CACHE_FILE);
                int itemsSaved = 0;
                if (!string.IsNullOrEmpty(path))
                {
                    itemsSaved = Client.Inventory.Store.Items.Count;
                    Client.Inventory.Store.SaveToDisk(path);
                }
                Feedback(wasGetDescriptionFromEnumValue(ConsoleError.INVENTORY_CACHE_ITEMS_SAVED),
                    itemsSaved.ToString(CultureInfo.InvariantCulture));
            }
        };

        private static readonly System.Action LoadCorradeCache = () =>
        {
            lock (Cache.Locks.AgentCacheLock)
            {
                Cache.AgentCache =
                    Cache.Load(Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY, CORRADE_CONSTANTS.AGENT_CACHE_FILE),
                        Cache.AgentCache);
            }
            lock (Cache.Locks.GroupCacheLock)
            {
                Cache.GroupCache =
                    Cache.Load(Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY, CORRADE_CONSTANTS.GROUP_CACHE_FILE),
                        Cache.GroupCache);
            }
        };

        private static readonly System.Action SaveCorradeCache = () =>
        {
            lock (Cache.Locks.AgentCacheLock)
            {
                Cache.Save(Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY, CORRADE_CONSTANTS.AGENT_CACHE_FILE),
                    Cache.AgentCache);
            }
            lock (Cache.Locks.GroupCacheLock)
            {
                Cache.Save(Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY, CORRADE_CONSTANTS.GROUP_CACHE_FILE),
                    Cache.GroupCache);
            }
        };

        private static volatile bool runCallbackThread = true;
        private static volatile bool runGroupMemberSweepThread = true;
        private static volatile bool runNotificationThread = true;

        public Corrade()
        {
            if (Environment.UserInteractive) return;
            CorradeServiceName = !string.IsNullOrEmpty(ServiceName)
                ? ServiceName
                : CORRADE_CONSTANTS.DEFAULT_SERVICE_NAME;
            CorradeLog.Source = CorradeServiceName;
            CorradeLog.Log = CORRADE_CONSTANTS.LOG_FACILITY;
            ((ISupportInitialize) (CorradeLog)).BeginInit();
            if (!EventLog.SourceExists(CorradeLog.Source))
            {
                EventLog.CreateEventSource(CorradeLog.Source, CorradeLog.Log);
            }
            ((ISupportInitialize) (CorradeLog)).EndInit();
        }

        /// <summary>
        ///     Gets the first name and last name from an avatar name.
        /// </summary>
        /// <param name="name">the avatar full name</param>
        /// <returns>the firstname and the lastname or resident</returns>
        private static IEnumerable<string> GetAvatarNames(string name)
        {
            return
                Regex.Matches(name,
                    @"^(?<first>.*?)([\s\.]|$)(?<last>.*?)$")
                    .Cast<Match>()
                    .ToDictionary(o => new[]
                    {
                        o.Groups["first"].Value,
                        o.Groups["last"].Value
                    })
                    .SelectMany(
                        o =>
                            new[]
                            {
                                o.Key[0],
                                !string.IsNullOrEmpty(o.Key[1])
                                    ? o.Key[1]
                                    : LINDEN_CONSTANTS.AVATARS.LASTNAME_PLACEHOLDER
                            });
        }

        private static bool ConsoleCtrlCheck(NativeMethods.CtrlType ctrlType)
        {
            KeyValuePair<char, ManualResetEvent> semaphore = ConnectionSemaphores.FirstOrDefault(o => o.Key.Equals('u'));
            if (semaphore.Value != null)
            {
                semaphore.Value.Set();
            }

            // Wait for threads to finish.
            Thread.Sleep(Configuration.SERVICES_TIMEOUT);
            return true;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2015 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Get the description from an enumeration value.
        /// </summary>
        /// <param name="value">an enumeration value</param>
        /// <returns>the description or the empty string</returns>
        private static string wasGetDescriptionFromEnumValue(Enum value)
        {
            DescriptionAttribute attribute = value.GetType()
                .GetField(value.ToString())
                .GetCustomAttributes(typeof (DescriptionAttribute), false)
                .SingleOrDefault() as DescriptionAttribute;
            return attribute != null ? attribute.Description : string.Empty;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2015 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Get enumeration value from its description.
        /// </summary>
        /// <typeparam name="T">the enumeration type</typeparam>
        /// <param name="description">the description of a member</param>
        /// <returns>the value or the default of T if case no description found</returns>
        private static T wasGetEnumValueFromDescription<T>(string description)
        {
            var field = typeof (T).GetFields()
                .SelectMany(f => f.GetCustomAttributes(
                    typeof (DescriptionAttribute), false), (
                        f, a) => new {Field = f, Att = a}).SingleOrDefault(a => ((DescriptionAttribute) a.Att)
                            .Description.Equals(description));
            return field != null ? (T) field.Field.GetRawConstantValue() : default(T);
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2015 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Get the description of structure member.
        /// </summary>
        /// <typeparam name="T">the type of the structure to search</typeparam>
        /// <param name="structure">the structure to search</param>
        /// <param name="item">the value of the item to search</param>
        /// <returns>the description or the empty string</returns>
        public static string wasGetStructureMemberDescription<T>(T structure, object item) where T : struct
        {
            var field = typeof (T).GetFields()
                .SelectMany(f => f.GetCustomAttributes(typeof (DescriptionAttribute), false),
                    (f, a) => new {Field = f, Att = a}).SingleOrDefault(f => f.Field.GetValue(structure).Equals(item));
            return field != null ? ((DescriptionAttribute) field.Att).Description : string.Empty;
        }

        /// <summary>
        ///     Gets or creates the outfit folder.
        /// </summary>
        /// <returns>the outfit folder or null if the folder did not exist and could not be created</returns>
        private static InventoryFolder GetOrCreateOutfitFolder(int millisecondsTimeout)
        {
            HashSet<InventoryBase> root = new HashSet<InventoryBase>();
            ManualResetEvent FolderUpdatedEvent = new ManualResetEvent(false);
            EventHandler<FolderUpdatedEventArgs> FolderUpdatedEventHandler = (sender, e) =>
            {
                if (e.FolderID.Equals(Client.Inventory.Store.RootFolder.UUID) && e.Success)
                {
                    root =
                        new HashSet<InventoryBase>(
                            Client.Inventory.Store.GetContents(Client.Inventory.Store.RootFolder.UUID));
                }
                FolderUpdatedEvent.Set();
            };

            lock (ServicesLock)
            {
                Client.Inventory.FolderUpdated += FolderUpdatedEventHandler;
                Client.Inventory.RequestFolderContents(Client.Inventory.Store.RootFolder.UUID, Client.Self.AgentID,
                    true, true, InventorySortOrder.ByDate);
                FolderUpdatedEvent.WaitOne(millisecondsTimeout);
                Client.Inventory.FolderUpdated -= FolderUpdatedEventHandler;
            }

            if (!root.Count.Equals(0))
            {
                InventoryFolder inventoryFolder =
                    root.FirstOrDefault(
                        o =>
                            o is InventoryFolder &&
                            ((InventoryFolder) o).PreferredType == AssetType.CurrentOutfitFolder) as InventoryFolder;
                if (inventoryFolder != null)
                {
                    return inventoryFolder;
                }
            }

            lock (InventoryLock)
            {
                UUID currentOutfitFolderUUID = Client.Inventory.CreateFolder(Client.Inventory.Store.RootFolder.UUID,
                    CORRADE_CONSTANTS.CURRENT_OUTFIT_FOLDER_NAME, AssetType.CurrentOutfitFolder);
                if (Client.Inventory.Store.Items.ContainsKey(currentOutfitFolderUUID) &&
                    Client.Inventory.Store.Items[currentOutfitFolderUUID].Data is InventoryFolder)
                {
                    return (InventoryFolder) Client.Inventory.Store.Items[currentOutfitFolderUUID].Data;
                }
            }

            return null;
        }

        /// <summary>
        ///     Can an inventory item be worn?
        /// </summary>
        /// <param name="item">item to check</param>
        /// <returns>true if the inventory item can be worn</returns>
        public static bool CanBeWorn(InventoryBase item)
        {
            return item is InventoryWearable || item is InventoryAttachment || item is InventoryObject;
        }

        /// <summary>
        ///     Resolves inventory links and returns a real inventory item that
        ///     the link is pointing to
        /// </summary>
        /// <param name="item">a link or inventory item</param>
        /// <returns>the real inventory item</returns>
        public static InventoryItem ResolveItemLink(InventoryItem item)
        {
            if (!item.AssetType.Equals(AssetType.Link)) return item;
            InventoryBase inventoryBase = FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, item.Name)
                .FirstOrDefault(o => (o is InventoryItem) && !(o as InventoryItem).AssetType.Equals(AssetType.Link));
            if (inventoryBase == null)
            {
                return null;
            }
            InventoryItem inventoryItem = inventoryBase as InventoryItem;
            if (inventoryItem == null)
            {
                return null;
            }
            return inventoryItem;
        }

        /// <summary>
        ///     Get current outfit folder links.
        /// </summary>
        /// <returns>a list of inventory items that can be part of appearance (attachments, wearables)</returns>
        public static List<InventoryItem> GetCurrentOutfitFolderLinks(int millisecondsTimeout)
        {
            List<InventoryItem> ret = new List<InventoryItem>();
            InventoryFolder COF = GetOrCreateOutfitFolder(millisecondsTimeout);
            if (COF == null) return ret;

            Client.Inventory.Store.GetContents(COF)
                .FindAll(b => CanBeWorn(b) && ((InventoryItem) b).AssetType.Equals(AssetType.Link))
                .ForEach(item => ret.Add((InventoryItem) item));

            return ret;
        }

        private static void Attach(InventoryItem item, AttachmentPoint point, bool replace, int millisecondsTimeout)
        {
            Client.Appearance.Attach(ResolveItemLink(item), point, replace);
            AddLink(item, millisecondsTimeout);
        }

        private static void Detach(InventoryItem item, int millisecondsTimeout)
        {
            RemoveLink(item, millisecondsTimeout);
            Client.Appearance.Detach(ResolveItemLink(item));
        }

        private static void Wear(InventoryItem item, bool replace, int millisecondsTimeout)
        {
            InventoryItem realItem = ResolveItemLink(item);
            if (item == null) return;
            Client.Appearance.AddToOutfit(realItem, replace);
            AddLink(realItem, millisecondsTimeout);
        }

        private static void UnWear(InventoryItem item, int millisecondsTimeout)
        {
            InventoryItem realItem = ResolveItemLink(item);
            if (realItem == null) return;
            Client.Appearance.RemoveFromOutfit(realItem);
            InventoryItem link = GetCurrentOutfitFolderLinks(millisecondsTimeout)
                .FirstOrDefault(o => o.AssetType.Equals(AssetType.Link) && o.Name.Equals(item.Name));
            if (link == null) return;
            RemoveLink(link, millisecondsTimeout);
        }

        /// <summary>
        ///     Is the item a body part?
        /// </summary>
        /// <param name="item">the item to check</param>
        /// <returns>true if the item is a body part</returns>
        private static bool IsBodyPart(InventoryItem item)
        {
            InventoryItem realItem = ResolveItemLink(item);
            if (!(realItem is InventoryWearable)) return false;
            WearableType t = ((InventoryWearable) realItem).WearableType;
            return t.Equals(WearableType.Shape) ||
                   t.Equals(WearableType.Skin) ||
                   t.Equals(WearableType.Eyes) ||
                   t.Equals(WearableType.Hair);
        }

        /// <summary>
        ///     Creates a new current outfit folder link.
        /// </summary>
        /// <param name="item">item to be linked</param>
        /// <param name="millisecondsTimeout">timeout in milliseconds</param>
        public static void lookupAddLink(InventoryItem item, int millisecondsTimeout)
        {
            InventoryFolder COF = GetOrCreateOutfitFolder(millisecondsTimeout);
            if (GetOrCreateOutfitFolder(millisecondsTimeout) == null) return;

            bool linkExists = null !=
                              GetCurrentOutfitFolderLinks(millisecondsTimeout)
                                  .Find(itemLink => itemLink.AssetUUID.Equals(item.UUID));

            if (linkExists) return;

            string description = (item.InventoryType.Equals(InventoryType.Wearable) && !IsBodyPart(item))
                ? string.Format("@{0}{1:00}", (int) ((InventoryWearable) item).WearableType, 0)
                : string.Empty;
            Client.Inventory.CreateLink(COF.UUID, item.UUID, item.Name, description, AssetType.Link,
                item.InventoryType, UUID.Random(), (success, newItem) =>
                {
                    if (success)
                    {
                        Client.Inventory.RequestFetchInventory(newItem.UUID, newItem.OwnerID);
                    }
                });
        }

        public static void AddLink(InventoryItem item, int millisecondsTimeout)
        {
            lock (InventoryLock)
            {
                lookupAddLink(item, millisecondsTimeout);
            }
        }

        /// <summary>
        ///     Remove a current outfit folder link of the specified inventory item.
        /// </summary>
        /// <param name="item">the inventory item for which to remove the link</param>
        /// <param name="millisecondsTimeout">timeout in milliseconds</param>
        public static void RemoveLink(InventoryItem item, int millisecondsTimeout)
        {
            RemoveLink(new HashSet<InventoryItem> {item}, millisecondsTimeout);
        }

        /// <summary>
        ///     Remove current outfit folder links for multiple specified inventory item.
        /// </summary>
        /// <param name="items">list of items whose links should be removed</param>
        /// <param name="millisecondsTimeout">timeout in milliseconds</param>
        public static void lookupRemoveLink(IEnumerable<InventoryItem> items, int millisecondsTimeout)
        {
            InventoryFolder COF = GetOrCreateOutfitFolder(millisecondsTimeout);
            if (COF == null) return;

            List<UUID> removeItems = new List<UUID>();
            object LockObject = new object();
            Parallel.ForEach(items,
                item =>
                    GetCurrentOutfitFolderLinks(millisecondsTimeout)
                        .FindAll(
                            itemLink =>
                                itemLink.AssetUUID.Equals(item is InventoryWearable ? item.AssetUUID : item.UUID))
                        .ForEach(link =>
                        {
                            lock (LockObject)
                            {
                                removeItems.Add(link.UUID);
                            }
                        }));

            Client.Inventory.Remove(removeItems, null);
        }

        public static void RemoveLink(IEnumerable<InventoryItem> items, int millisecondsTimeout)
        {
            lock (InventoryLock)
            {
                lookupRemoveLink(items, millisecondsTimeout);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Swaps two integers passed by reference using XOR.
        /// </summary>
        /// <param name="q">first integer to swap</param>
        /// <param name="p">second integer to swap</param>
        private static void wasXORSwap(ref int q, ref int p)
        {
            q ^= p;
            p ^= q;
            q ^= p;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns all the field descriptions of an enumeration.
        /// </summary>
        /// <returns>the field descriptions</returns>
        private static IEnumerable<string> wasGetEnumDescriptions<T>()
        {
            return typeof (T).GetFields(BindingFlags.Static | BindingFlags.Public)
                .Select(o => wasGetDescriptionFromEnumValue((Enum) o.GetValue(null)));
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Enumerates the fields of an object along with the child objects,
        ///     provided that all child objects are part of a specified namespace.
        /// </summary>
        /// <param name="object">the object to enumerate</param>
        /// <param name="namespace">the namespace to enumerate in</param>
        /// <returns>child objects of the object</returns>
        private static IEnumerable<KeyValuePair<FieldInfo, object>> wasGetFields(object @object, string @namespace)
        {
            if (@object == null) yield break;

            foreach (FieldInfo fi in @object.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (fi.FieldType.FullName.Split(new[] {'.', '+'})
                    .Contains(@namespace, StringComparer.InvariantCultureIgnoreCase))
                {
                    foreach (KeyValuePair<FieldInfo, object> sf in wasGetFields(fi.GetValue(@object), @namespace))
                    {
                        yield return sf;
                    }
                }
                yield return new KeyValuePair<FieldInfo, object>(fi, @object);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Enumerates the properties of an object along with the child objects,
        ///     provided that all child objects are part of a specified namespace.
        /// </summary>
        /// <param name="object">the object to enumerate</param>
        /// <param name="namespace">the namespace to enumerate in</param>
        /// <returns>child objects of the object</returns>
        private static IEnumerable<KeyValuePair<PropertyInfo, object>> wasGetProperties(object @object,
            string @namespace)
        {
            if (@object == null) yield break;

            foreach (PropertyInfo pi in @object.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (pi.PropertyType.FullName.Split(new[] {'.', '+'})
                    .Contains(@namespace, StringComparer.InvariantCultureIgnoreCase))
                {
                    foreach (
                        KeyValuePair<PropertyInfo, object> sp in
                            wasGetProperties(pi.GetValue(@object, null), @namespace))
                    {
                        yield return sp;
                    }
                }
                yield return new KeyValuePair<PropertyInfo, object>(pi, @object);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     This is a wrapper for both FieldInfo and PropertyInfo SetValue.
        /// </summary>
        /// <param name="info">either a FieldInfo or PropertyInfo</param>
        /// <param name="object">the object to set the value on</param>
        /// <param name="value">the value to set</param>
        private static void wasSetInfoValue<I, T>(I info, ref T @object, object value)
        {
            object o = @object;
            FieldInfo fi = (object) info as FieldInfo;
            if (fi != null)
            {
                fi.SetValue(o, value);
                @object = (T) o;
                return;
            }
            PropertyInfo pi = (object) info as PropertyInfo;
            if (pi != null)
            {
                pi.SetValue(o, value, null);
                @object = (T) o;
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     This is a wrapper for both FieldInfo and PropertyInfo GetValue.
        /// </summary>
        /// <param name="info">either a FieldInfo or PropertyInfo</param>
        /// <param name="value">the object to get from</param>
        /// <returns>the value of the field or property</returns>
        private static object wasGetInfoValue<T>(T info, object value)
        {
            FieldInfo fi = (object) info as FieldInfo;
            if (fi != null)
            {
                return fi.GetValue(value);
            }
            PropertyInfo pi = (object) info as PropertyInfo;
            if (pi != null)
            {
                return pi.GetValue(value, null);
            }
            return null;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     The function gets the value from FieldInfo or PropertyInfo.
        /// </summary>
        /// <param name="info">a FieldInfo or PropertyInfo structure</param>
        /// <param name="value">the value to get</param>
        /// <returns>the value or values as a string</returns>
        private static IEnumerable<string> wasGetInfo(object info, object value)
        {
            if (info == null) yield break;
            object data = wasGetInfoValue(info, value);
            // Handle arrays
            Array list = data as Array;
            if (list != null)
            {
                IList array = (IList) data;
                if (array.Count.Equals(0)) yield break;
                foreach (
                    string itemValue in
                        array.Cast<object>()
                            .Select(item => item.ToString())
                            .Where(itemValue => !string.IsNullOrEmpty(itemValue)))
                {
                    yield return itemValue;
                }
                yield break;
            }
            string @string = data.ToString();
            if (string.IsNullOrEmpty(@string)) yield break;
            yield return @string;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Sets the value of FieldInfo or PropertyInfo.
        /// </summary>
        /// <typeparam name="T">the type to set</typeparam>
        /// <param name="info">a FieldInfo or PropertyInfo object</param>
        /// <param name="value">the object's value</param>
        /// <param name="setting">the value to set to</param>
        /// <param name="object">the object to set the values for</param>
        private static void wasSetInfo<T>(object info, object value, string setting, ref T @object)
        {
            if (info == null) return;
            if (wasGetInfoValue(info, value) is string)
            {
                wasSetInfoValue(info, ref @object, setting);
            }
            if (wasGetInfoValue(info, value) is UUID)
            {
                UUID UUIDData;
                if (!UUID.TryParse(setting, out UUIDData))
                {
                    InventoryItem item = FindInventory<InventoryBase>(Client.Inventory.Store.RootNode,
                        setting).FirstOrDefault() as InventoryItem;
                    if (item == null)
                    {
                        throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                    }
                    UUIDData = item.UUID;
                }
                if (UUIDData.Equals(UUID.Zero))
                {
                    throw new Exception(
                        wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                }
                wasSetInfoValue(info, ref @object, UUIDData);
            }
            if (wasGetInfoValue(info, value) is bool)
            {
                bool boolData;
                if (bool.TryParse(setting, out boolData))
                {
                    wasSetInfoValue(info, ref @object, boolData);
                }
            }
            if (wasGetInfoValue(info, value) is int)
            {
                int intData;
                if (int.TryParse(setting, out intData))
                {
                    wasSetInfoValue(info, ref @object, intData);
                }
            }
            if (wasGetInfoValue(info, value) is uint)
            {
                uint uintData;
                if (uint.TryParse(setting, out uintData))
                {
                    wasSetInfoValue(info, ref @object, uintData);
                }
            }
            if (wasGetInfoValue(info, value) is DateTime)
            {
                DateTime dateTimeData;
                if (DateTime.TryParse(setting, out dateTimeData))
                {
                    wasSetInfoValue(info, ref @object, dateTimeData);
                }
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Determines whether an agent has a set of powers for a group.
        /// </summary>
        /// <param name="agentUUID">the agent UUID</param>
        /// <param name="groupUUID">the UUID of the group</param>
        /// <param name="powers">a GroupPowers structure</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <returns>true if the agent has the powers</returns>
        private static bool lookupHasGroupPowers(UUID agentUUID, UUID groupUUID, GroupPowers powers,
            int millisecondsTimeout)
        {
            bool hasPowers = false;
            ManualResetEvent avatarGroupsEvent = new ManualResetEvent(false);
            EventHandler<AvatarGroupsReplyEventArgs> AvatarGroupsReplyEventHandler = (sender, args) =>
            {
                hasPowers =
                    args.Groups.Any(
                        o => o.GroupID.Equals(groupUUID) && !(o.GroupPowers & powers).Equals(GroupPowers.None));
                avatarGroupsEvent.Set();
            };
            Client.Avatars.AvatarGroupsReply += AvatarGroupsReplyEventHandler;
            Client.Avatars.RequestAvatarProperties(agentUUID);
            if (!avatarGroupsEvent.WaitOne(millisecondsTimeout, false))
            {
                Client.Avatars.AvatarGroupsReply -= AvatarGroupsReplyEventHandler;
                return false;
            }
            Client.Avatars.AvatarGroupsReply -= AvatarGroupsReplyEventHandler;
            return hasPowers;
        }

        /// <summary>
        ///     Determines whether n agent has a set of powers for a group - locks down services.
        /// </summary>
        /// <param name="agentUUID">the agent UUID</param>
        /// <param name="groupUUID">the UUID of the group</param>
        /// <param name="powers">a GroupPowers structure</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <returns>true if the agent has the powers</returns>
        private static bool HasGroupPowers(UUID agentUUID, UUID groupUUID, GroupPowers powers, int millisecondsTimeout)
        {
            lock (ServicesLock)
            {
                return lookupHasGroupPowers(agentUUID, groupUUID, powers, millisecondsTimeout);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Determines whether an agent referenced by an UUID is in a group
        ///     referenced by an UUID.
        /// </summary>
        /// <param name="agentUUID">the UUID of the agent</param>
        /// <param name="groupUUID">the UUID of the groupt</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <returns>true if the agent is in the group</returns>
        private static bool lookupAgentInGroup(UUID agentUUID, UUID groupUUID, int millisecondsTimeout)
        {
            bool agentInGroup = false;
            ManualResetEvent agentInGroupEvent = new ManualResetEvent(false);
            EventHandler<GroupMembersReplyEventArgs> HandleGroupMembersReplyDelegate = (sender, args) =>
            {
                agentInGroup = args.Members.Any(o => o.Value.ID.Equals(agentUUID));
                agentInGroupEvent.Set();
            };
            Client.Groups.GroupMembersReply += HandleGroupMembersReplyDelegate;
            Client.Groups.RequestGroupMembers(groupUUID);
            if (!agentInGroupEvent.WaitOne(millisecondsTimeout, false))
            {
                Client.Groups.GroupMembersReply -= HandleGroupMembersReplyDelegate;
                return false;
            }
            Client.Groups.GroupMembersReply -= HandleGroupMembersReplyDelegate;
            return agentInGroup;
        }

        /// <summary>
        ///     Determines whether an agent is in a group - locks down services.
        /// </summary>
        /// <param name="agentUUID">the UUID of the agent</param>
        /// <param name="groupUUID">the UUID of the groupt</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <returns>true if the agent is in the group</returns>
        private static bool AgentInGroup(UUID agentUUID, UUID groupUUID, int millisecondsTimeout)
        {
            lock (ServicesLock)
            {
                return lookupAgentInGroup(agentUUID, groupUUID, millisecondsTimeout);
            }
        }

        /// <summary>
        ///     Used to check whether a group name matches a group password.
        /// </summary>
        /// <param name="group">the name of the group</param>
        /// <param name="password">the password for the group</param>
        /// <returns>true if the agent has authenticated</returns>
        private static bool Authenticate(string group, string password)
        {
            UUID groupUUID;
            return UUID.TryParse(group, out groupUUID)
                ? Configuration.GROUPS.Any(
                    o =>
                        groupUUID.Equals(o.UUID) &&
                        password.Equals(o.Password, StringComparison.Ordinal))
                : Configuration.GROUPS.Any(
                    o =>
                        o.Name.Equals(group, StringComparison.Ordinal) &&
                        password.Equals(o.Password, StringComparison.Ordinal));
        }

        /// <summary>
        ///     Used to check whether a group has certain permissions for Corrade.
        /// </summary>
        /// <param name="group">the name of the group</param>
        /// <param name="permission">the numeric Corrade permission</param>
        /// <returns>true if the group has permission</returns>
        private static bool HasCorradePermission(string group, int permission)
        {
            UUID groupUUID;
            return !permission.Equals(0) && UUID.TryParse(group, out groupUUID)
                ? Configuration.GROUPS.Any(o => groupUUID.Equals(o.UUID) && !(o.PermissionMask & permission).Equals(0))
                : Configuration.GROUPS.Any(
                    o =>
                        o.Name.Equals(group, StringComparison.Ordinal) &&
                        !(o.PermissionMask & permission).Equals(0));
        }

        /// <summary>
        ///     Used to check whether a group has a certain notification for Corrade.
        /// </summary>
        /// <param name="group">the name of the group</param>
        /// <param name="notification">the numeric Corrade notification</param>
        /// <returns>true if the group has the notification</returns>
        private static bool HasCorradeNotification(string group, uint notification)
        {
            UUID groupUUID;
            return !notification.Equals(0) && UUID.TryParse(group, out groupUUID)
                ? Configuration.GROUPS.Any(
                    o => groupUUID.Equals(o.UUID) &&
                         !(o.NotificationMask & notification).Equals(0))
                : Configuration.GROUPS.Any(
                    o => o.Name.Equals(group, StringComparison.Ordinal) &&
                         !(o.NotificationMask & notification).Equals(0));
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Fetches a group.
        /// </summary>
        /// <param name="groupUUID">the UUID of the group</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="group">a group object to store the group profile</param>
        /// <returns>true if the group was found and false otherwise</returns>
        private static bool lookupRequestGroup(UUID groupUUID, int millisecondsTimeout, ref OpenMetaverse.Group group)
        {
            OpenMetaverse.Group localGroup = new OpenMetaverse.Group();
            ManualResetEvent GroupProfileEvent = new ManualResetEvent(false);
            EventHandler<GroupProfileEventArgs> GroupProfileDelegate = (sender, args) =>
            {
                localGroup = args.Group;
                GroupProfileEvent.Set();
            };
            Client.Groups.GroupProfile += GroupProfileDelegate;
            Client.Groups.RequestGroupProfile(groupUUID);
            if (!GroupProfileEvent.WaitOne(millisecondsTimeout, false))
            {
                Client.Groups.GroupProfile -= GroupProfileDelegate;
                return false;
            }
            Client.Groups.GroupProfile -= GroupProfileDelegate;
            group = localGroup;
            return true;
        }

        /// <summary>
        ///     Wrapper for group profile requests - locks down service usage.
        /// </summary>
        /// <param name="groupUUID">the UUID of the group</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="group">a group object to store the group profile</param>
        /// <returns>true if the group was found and false otherwise</returns>
        private static bool RequestGroup(UUID groupUUID, int millisecondsTimeout, ref OpenMetaverse.Group group)
        {
            lock (ServicesLock)
            {
                return lookupRequestGroup(groupUUID, millisecondsTimeout, ref group);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Get the parcel of a simulator given a position.
        /// </summary>
        /// <param name="simulator">the simulator containing the parcel</param>
        /// <param name="position">a position within the parcel</param>
        /// <param name="parcel">a parcel object where to store the found parcel</param>
        /// <returns>true if the parcel could be found</returns>
        private static bool lookupGetParcelAtPosition(Simulator simulator, Vector3 position,
            ref Parcel parcel)
        {
            Parcel localParcel = null;
            ManualResetEvent RequestAllSimParcelsEvent = new ManualResetEvent(false);
            EventHandler<SimParcelsDownloadedEventArgs> SimParcelsDownloadedDelegate =
                (sender, args) => RequestAllSimParcelsEvent.Set();
            Client.Parcels.SimParcelsDownloaded += SimParcelsDownloadedDelegate;
            Client.Parcels.RequestAllSimParcels(simulator);
            if (Client.Network.CurrentSim.IsParcelMapFull())
            {
                RequestAllSimParcelsEvent.Set();
            }
            if (!RequestAllSimParcelsEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
            {
                Client.Parcels.SimParcelsDownloaded -= SimParcelsDownloadedDelegate;
                return false;
            }
            Client.Parcels.SimParcelsDownloaded -= SimParcelsDownloadedDelegate;
            Client.Network.CurrentSim.Parcels.ForEach(currentParcel =>
            {
                if (!(position.X >= currentParcel.AABBMin.X) || !(position.X <= currentParcel.AABBMax.X) ||
                    !(position.Y >= currentParcel.AABBMin.Y) || !(position.Y <= currentParcel.AABBMax.Y))
                    return;
                localParcel = currentParcel;
            });
            if (localParcel == null)
                return false;
            parcel = localParcel;
            return true;
        }

        /// <summary>
        ///     Wrapper for getting a parcel at a position - locks down services.
        /// </summary>
        /// <param name="simulator">the simulator containing the parcel</param>
        /// <param name="position">a position within the parcel</param>
        /// <param name="parcel">a parcel object where to store the found parcel</param>
        /// <returns>true if the parcel could be found</returns>
        private static bool GetParcelAtPosition(Simulator simulator, Vector3 position, ref Parcel parcel)
        {
            lock (ServicesLock)
            {
                return lookupGetParcelAtPosition(simulator, position, ref parcel);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Find a named primitive in range (whether attachment or in-world).
        /// </summary>
        /// <param name="item">the name or UUID of the primitive</param>
        /// <param name="range">the range in meters to search for the object</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="primitive">a primitive object to store the result</param>
        /// <returns>true if the primitive could be found</returns>
        private static bool lookupFindPrimitive(string item, float range, int millisecondsTimeout,
            ref Primitive primitive)
        {
            UUID itemUUID;
            if (!UUID.TryParse(item, out itemUUID))
            {
                itemUUID = UUID.Zero;
            }
            Hashtable queue = new Hashtable();
            Client.Network.CurrentSim.ObjectsPrimitives.ForEach(o =>
            {
                switch (o.ParentID)
                {
                        // primitive is a parent and it is in range
                    case 0:
                        if (Vector3.Distance(o.Position, Client.Self.SimPosition) < range)
                        {
                            if (itemUUID.Equals(UUID.Zero))
                            {
                                queue.Add(o.ID, o.LocalID);
                                break;
                            }
                            if (!itemUUID.Equals(UUID.Zero) && o.ID.Equals(itemUUID))
                            {
                                queue.Add(o.ID, o.LocalID);
                            }
                        }
                        break;
                        // primitive is a child
                    default:
                        // find the parent of the primitive
                        Primitive parent = o;
                        do
                        {
                            Primitive closure = parent;
                            Primitive ancestor =
                                Client.Network.CurrentSim.ObjectsPrimitives.Find(p => p.LocalID.Equals(closure.ParentID));
                            if (ancestor == null) break;
                            parent = ancestor;
                        } while (!parent.ParentID.Equals(0));
                        // the parent primitive has no other parent
                        if (parent.ParentID.Equals(0))
                        {
                            // if the parent is in range, add the child
                            if (Vector3.Distance(parent.Position, Client.Self.SimPosition) < range)
                            {
                                if (itemUUID.Equals(UUID.Zero))
                                {
                                    queue.Add(o.ID, o.LocalID);
                                    break;
                                }
                                if (!itemUUID.Equals(UUID.Zero) && o.ID.Equals(itemUUID))
                                {
                                    queue.Add(o.ID, o.LocalID);
                                }
                                break;
                            }
                        }
                        // check if an avatar is the parent of the parent primitive
                        Avatar parentAvatar =
                            Client.Network.CurrentSim.ObjectsAvatars.Find(p => p.LocalID.Equals(parent.ParentID));
                        // parent avatar not found, this should not happen
                        if (parentAvatar == null) break;
                        // check if the avatar is in range
                        if (Vector3.Distance(parentAvatar.Position, Client.Self.SimPosition) < range)
                        {
                            if (itemUUID.Equals(UUID.Zero))
                            {
                                queue.Add(o.ID, o.LocalID);
                                break;
                            }
                            if (!itemUUID.Equals(UUID.Zero) && o.ID.Equals(itemUUID))
                            {
                                queue.Add(o.ID, o.LocalID);
                            }
                        }
                        break;
                }
            });
            if (queue.Count.Equals(0))
                return false;
            ManualResetEvent ObjectPropertiesEvent = new ManualResetEvent(false);
            EventHandler<ObjectPropertiesEventArgs> ObjectPropertiesEventHandler = (sender, args) =>
            {
                queue.Remove(args.Properties.ObjectID);
                if (!args.Properties.Name.Equals(item, StringComparison.Ordinal) &&
                    (itemUUID.Equals(UUID.Zero) || !args.Properties.ItemID.Equals(itemUUID)) && !queue.Count.Equals(0))
                    return;
                ObjectPropertiesEvent.Set();
            };
            Client.Objects.ObjectProperties += ObjectPropertiesEventHandler;
            Client.Objects.SelectObjects(Client.Network.CurrentSim, queue.Values.Cast<uint>().ToArray(), true);
            if (
                !ObjectPropertiesEvent.WaitOne(
                    millisecondsTimeout, false))
            {
                Client.Objects.ObjectProperties -= ObjectPropertiesEventHandler;
                return false;
            }
            Client.Objects.ObjectProperties -= ObjectPropertiesEventHandler;
            primitive =
                Client.Network.CurrentSim.ObjectsPrimitives.Find(
                    o =>
                        o.ID.Equals(itemUUID) ||
                        (o.Properties != null && o.Properties.Name.Equals(item, StringComparison.Ordinal)));
            return primitive != null;
        }

        /// <summary>
        ///     Wrapper for finding primitives given an item, range, timeout and primitive to store - locks services down.
        /// </summary>
        /// <param name="item">the name or UUID of the primitive</param>
        /// <param name="range">the range in meters to search for the object</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="primitive">a primitive object to store the result</param>
        /// <returns>true if the primitive could be found</returns>
        private static bool FindPrimitive(string item, float range, int millisecondsTimeout, ref Primitive primitive)
        {
            lock (ServicesLock)
            {
                return lookupFindPrimitive(item, range, millisecondsTimeout, ref primitive);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Requests the groups that Corrade is a member of.
        /// </summary>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="groups">a hash set to hold the current groups</param>
        /// <returns>true if the current groups could have been successfully retrieved</returns>
        private static bool lookupRequestCurrentGroups(int millisecondsTimeout, ref HashSet<OpenMetaverse.Group> groups)
        {
            ManualResetEvent CurrentGroupsEvent = new ManualResetEvent(false);
            HashSet<OpenMetaverse.Group> localGroups = new HashSet<OpenMetaverse.Group>();
            EventHandler<CurrentGroupsEventArgs> CurrentGroupsEventHandler = (s, a) =>
            {
                localGroups = new HashSet<OpenMetaverse.Group>(a.Groups.Select(o => o.Value));
                CurrentGroupsEvent.Set();
            };
            Client.Groups.CurrentGroups += CurrentGroupsEventHandler;
            Client.Groups.RequestCurrentGroups();
            if (!CurrentGroupsEvent.WaitOne(millisecondsTimeout, false))
            {
                Client.Groups.CurrentGroups -= CurrentGroupsEventHandler;
                return false;
            }
            Client.Groups.CurrentGroups -= CurrentGroupsEventHandler;
            groups = localGroups;
            return true;
        }

        /// <summary>
        ///     Wrapper for requesting current groups - used for locking down services.
        /// </summary>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="groups">a hash set to hold the current groups</param>
        /// <returns>true if the current groups could have been successfully retrieved</returns>
        private static bool RequestCurrentGroups(int millisecondsTimeout, ref HashSet<OpenMetaverse.Group> groups)
        {
            lock (ServicesLock)
            {
                return lookupRequestCurrentGroups(millisecondsTimeout, ref groups);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Get all worn attachments.
        /// </summary>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <returns>attachment points by primitives</returns>
        private static IEnumerable<KeyValuePair<Primitive, AttachmentPoint>> lookupGetAttachments(
            int millisecondsTimeout)
        {
            HashSet<Primitive> primitives = new HashSet<Primitive>(Client.Network.CurrentSim.ObjectsPrimitives.FindAll(
                o => o.ParentID.Equals(Client.Self.LocalID)));
            Hashtable primitiveQueue = new Hashtable(primitives.ToDictionary(o => o.ID, o => o.LocalID));
            ManualResetEvent ObjectPropertiesEvent = new ManualResetEvent(false);
            EventHandler<ObjectPropertiesEventArgs> ObjectPropertiesEventHandler = (sender, args) =>
            {
                primitiveQueue.Remove(args.Properties.ObjectID);
                if (!primitiveQueue.Count.Equals(0)) return;
                ObjectPropertiesEvent.Set();
            };
            Client.Objects.ObjectProperties += ObjectPropertiesEventHandler;
            Client.Objects.SelectObjects(Client.Network.CurrentSim, primitiveQueue.Values.Cast<uint>().ToArray(), true);
            if (ObjectPropertiesEvent.WaitOne(millisecondsTimeout, false))
            {
                Client.Objects.ObjectProperties -= ObjectPropertiesEventHandler;
                foreach (Primitive primitive in primitives)
                {
                    yield return new KeyValuePair<Primitive, AttachmentPoint>(
                        primitive,
                        (AttachmentPoint) (((primitive.PrimData.State & 0xF0) >> 4) |
                                           ((primitive.PrimData.State & ~0xF0) << 4))
                        );
                }
            }
            Client.Objects.ObjectProperties -= ObjectPropertiesEventHandler;
        }

        /// <summary>
        ///     Get worn attachments - locks down services.
        /// </summary>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <returns>attachment points by primitives</returns>
        private static IEnumerable<KeyValuePair<Primitive, AttachmentPoint>> GetAttachments(int millisecondsTimeout)
        {
            lock (ServicesLock)
            {
                return lookupGetAttachments(millisecondsTimeout);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Gets the inventory wearables that are currently being worn.
        /// </summary>
        /// <param name="root">the folder to start the search from</param>
        /// <returns>key value pairs of wearables by name</returns>
        private static IEnumerable<KeyValuePair<WearableType, string>> lookupGetWearables(InventoryNode root)
        {
            InventoryFolder inventoryFolder = Client.Inventory.Store[root.Data.UUID] as InventoryFolder;
            if (inventoryFolder == null)
            {
                InventoryItem inventoryItem = Client.Inventory.Store[root.Data.UUID] as InventoryItem;
                if (inventoryItem != null)
                {
                    WearableType wearableType = Client.Appearance.IsItemWorn(inventoryItem);
                    if (!wearableType.Equals(WearableType.Invalid))
                    {
                        yield return new KeyValuePair<WearableType, string>(wearableType, inventoryItem.Name);
                    }
                }
            }
            foreach (
                KeyValuePair<WearableType, string> item in
                    root.Nodes.Values.SelectMany(node => lookupGetWearables(node)))
            {
                yield return item;
            }
        }

        private static IEnumerable<KeyValuePair<WearableType, string>> GetWearables(InventoryNode root)
        {
            lock (InventoryLock)
            {
                return lookupGetWearables(root);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// ///
        /// <summary>
        ///     Fetches items by searching the inventory starting with an inventory
        ///     node where the search criteria finds:
        ///     - name as string
        ///     - name as Regex
        ///     - UUID as UUID
        /// </summary>
        /// <param name="root">the node to start the search from</param>
        /// <param name="criteria">the name, UUID or Regex of the item to be found</param>
        /// <returns>a list of items matching the item name</returns>
        private static IEnumerable<T> lookupFindInventory<T>(InventoryNode root, object criteria)
        {
            if ((criteria is Regex && (criteria as Regex).IsMatch(root.Data.Name)) ||
                (criteria is string &&
                 (criteria as string).Equals(root.Data.Name, StringComparison.Ordinal)) ||
                (criteria is UUID && criteria.Equals(root.Data.UUID)))
            {
                if (typeof (T) == typeof (InventoryNode))
                {
                    yield return (T) (object) root;
                }
                if (typeof (T) == typeof (InventoryBase))
                {
                    yield return (T) (object) Client.Inventory.Store[root.Data.UUID];
                }
            }
            foreach (T item in root.Nodes.Values.SelectMany(node => lookupFindInventory<T>(node, criteria)))
            {
                yield return item;
            }
        }

        private static IEnumerable<T> FindInventory<T>(InventoryNode root, object criteria)
        {
            lock (InventoryLock)
            {
                return lookupFindInventory<T>(root, criteria);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// ///
        /// <summary>
        ///     Fetches items and their full path from the inventory starting with
        ///     an inventory node where the search criteria finds:
        ///     - name as string
        ///     - name as Regex
        ///     - UUID as UUID
        /// </summary>
        /// <param name="root">the node to start the search from</param>
        /// <param name="criteria">the name, UUID or Regex of the item to be found</param>
        /// <param name="prefix">any prefix to append to the found paths</param>
        /// <returns>items matching criteria and their full inventoy path</returns>
        private static IEnumerable<KeyValuePair<T, LinkedList<string>>> lookupFindInventoryPath<T>(
            InventoryNode root, object criteria, LinkedList<string> prefix)
        {
            if ((criteria is Regex && (criteria as Regex).IsMatch(root.Data.Name)) ||
                (criteria is string &&
                 (criteria as string).Equals(root.Data.Name, StringComparison.Ordinal)) ||
                (criteria is UUID && criteria.Equals(root.Data.UUID)))
            {
                if (typeof (T) == typeof (InventoryBase))
                {
                    yield return
                        new KeyValuePair<T, LinkedList<string>>((T) (object) Client.Inventory.Store[root.Data.UUID],
                            new LinkedList<string>(
                                prefix.Concat(new[] {root.Data.Name})));
                }
                if (typeof (T) == typeof (InventoryNode))
                {
                    yield return
                        new KeyValuePair<T, LinkedList<string>>((T) (object) root,
                            new LinkedList<string>(
                                prefix.Concat(new[] {root.Data.Name})));
                }
            }
            foreach (
                KeyValuePair<T, LinkedList<string>> o in
                    root.Nodes.Values.SelectMany(o => lookupFindInventoryPath<T>(o, criteria, new LinkedList<string>(
                        prefix.Concat(new[] {root.Data.Name})))))
            {
                yield return o;
            }
        }

        private static IEnumerable<KeyValuePair<T, LinkedList<string>>> FindInventoryPath<T>(
            InventoryNode root, object citeria, LinkedList<string> path)
        {
            lock (InventoryLock)
            {
                return lookupFindInventoryPath<T>(root, citeria, path);
            }
        }


        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Gets all the items from an inventory folder and returns the items.
        /// </summary>
        /// <param name="rootFolder">a folder from which to search</param>
        /// <param name="folder">the folder to search for</param>
        /// <returns>a list of items from the folder</returns>
        private static IEnumerable<T> lookupGetInventoryFolderContents<T>(InventoryNode rootFolder,
            string folder)
        {
            foreach (
                InventoryNode node in
                    rootFolder.Nodes.Values.Where(node => node.Data is InventoryFolder && node.Data.Name.Equals(folder))
                )
            {
                foreach (InventoryNode item in node.Nodes.Values)
                {
                    if (typeof (T) == typeof (InventoryNode))
                    {
                        yield return (T) (object) item;
                    }
                    if (typeof (T) == typeof (InventoryBase))
                    {
                        yield return (T) (object) Client.Inventory.Store[item.Data.UUID];
                    }
                }
                break;
            }
        }

        private static IEnumerable<T> GetInventoryFolderContents<T>(InventoryNode rootFolder, string folder)
        {
            lock (InventoryLock)
            {
                return lookupGetInventoryFolderContents<T>(rootFolder, folder);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2015 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Rebakes the avatar and returns the success status.
        /// </summary>
        /// <param name="millisecondsTimeout">time to wait for the rebake</param>
        /// <returns>true if the rebake was successful</returns>
        private static bool lookupRebake(int millisecondsTimeout)
        {
            bool succeeded = false;
            ManualResetEvent AppearanceSetEvent = new ManualResetEvent(false);
            EventHandler<AppearanceSetEventArgs> HandleAppearanceSet = (sender, args) =>
            {
                succeeded = args.Success;
                AppearanceSetEvent.Set();
            };
            Client.Appearance.AppearanceSet += HandleAppearanceSet;
            Client.Appearance.RequestSetAppearance(true);
            if (!AppearanceSetEvent.WaitOne(millisecondsTimeout, false))
            {
                Client.Appearance.AppearanceSet -= HandleAppearanceSet;
                return false;
            }
            Client.Appearance.AppearanceSet -= HandleAppearanceSet;
            return succeeded;
        }

        private static bool Rebake(int millisecondsTimeout, int delay)
        {
            ManualResetEvent RebakedEvent = new ManualResetEvent(false);
            bool succeeded = false;
            new Thread(() =>
            {
                Thread.Sleep(delay);
                lock (InventoryLock)
                {
                    lock (ServicesLock)
                    {
                        succeeded = lookupRebake(millisecondsTimeout);
                    }
                }
                RebakedEvent.Set();
            }) {IsBackground = true}.Start();
            RebakedEvent.WaitOne(millisecondsTimeout, false);
            return succeeded;
        }

        private static bool lookupActivateCurrentLandGroup()
        {
            Parcel parcel = null;
            if (!GetParcelAtPosition(Client.Network.CurrentSim, Client.Self.SimPosition, ref parcel))
            {
                return false;
            }
            UUID groupUUID = Configuration.GROUPS.FirstOrDefault(o => o.UUID.Equals(parcel.GroupID)).UUID;
            if (groupUUID.Equals(UUID.Zero))
            {
                return false;
            }
            Client.Groups.ActivateGroup(groupUUID);
            return true;
        }

        private static bool ActivateCurrentLandGroup(int millisecondsTimeout, int delay)
        {
            ManualResetEvent ActivateCurrentLandGroupEvent = new ManualResetEvent(false);
            bool succeeded = false;
            new Thread(() =>
            {
                Thread.Sleep(delay);
                lock (TeleportLock)
                {
                    lock (ServicesLock)
                    {
                        succeeded = lookupActivateCurrentLandGroup();
                    }
                }
                ActivateCurrentLandGroupEvent.Set();
            }) {IsBackground = true}.Start();
            ActivateCurrentLandGroupEvent.WaitOne(millisecondsTimeout, false);
            return succeeded;
        }

        /// <summary>
        ///     Posts messages to console or log-files.
        /// </summary>
        /// <param name="messages">a list of messages</param>
        private static void Feedback(params string[] messages)
        {
            List<string> output = new List<string>
            {
                "Corrade",
                string.Format(CultureInfo.InvariantCulture, "[{0}]",
                    DateTime.Now.ToString(CORRADE_CONSTANTS.DATE_TIME_STAMP, DateTimeFormatInfo.InvariantInfo)),
            };

            output.AddRange(messages.Select(message => message));

            // Attempt to write to log file,
            try
            {
                lock (LogFileLock)
                {
                    using (
                        StreamWriter logWriter =
                            File.AppendText(Configuration.LOG_FILE))
                    {
                        logWriter.WriteLine(string.Join(CORRADE_CONSTANTS.ERROR_SEPARATOR, output.ToArray()));
                        logWriter.Flush();
                    }
                }
            }
            catch (Exception e)
            {
                // or fail and append the fail message.
                output.Add(string.Format(CultureInfo.InvariantCulture,
                    "The request could not be logged to {0} and returned the error message {1}.",
                    Configuration.LOG_FILE, e.Message));
            }

            if (!Environment.UserInteractive)
            {
                switch (Environment.OSVersion.Platform)
                {
                    case PlatformID.Win32NT:
                        CorradeLog.WriteEntry(string.Join(CORRADE_CONSTANTS.ERROR_SEPARATOR, output.ToArray()),
                            EventLogEntryType.Information);
                        break;
                    case PlatformID.Unix:
                        Syscall.syslog(SyslogFacility.LOG_DAEMON, SyslogLevel.LOG_INFO,
                            string.Join(CORRADE_CONSTANTS.ERROR_SEPARATOR, output.ToArray()));
                        break;
                }
                return;
            }

            Console.WriteLine(string.Join(CORRADE_CONSTANTS.ERROR_SEPARATOR, output.ToArray()));
        }

        /// <summary>
        ///     Writes the logo and the version.
        /// </summary>
        private static void WriteLogo()
        {
            List<string> logo = new List<string>
            {
                Environment.NewLine,
                Environment.NewLine,
                @"       _..--=--..._  " + Environment.NewLine,
                @"    .-'            '-.  .-.  " + Environment.NewLine,
                @"   /.'              '.\/  /  " + Environment.NewLine,
                @"  |=-     Corrade    -=| (  " + Environment.NewLine,
                @"   \'.              .'/\  \  " + Environment.NewLine,
                @"    '-.,_____ _____.-'  '-'  " + Environment.NewLine,
                @"          [_____]=8  " + Environment.NewLine,
                @"               \  " + Environment.NewLine,
                @"                 Good day!  ",
                Environment.NewLine,
                Environment.NewLine,
                string.Format(CultureInfo.InvariantCulture,
                    Environment.NewLine + "Version: {0} Compiled: {1}" + Environment.NewLine, CORRADE_VERSION,
                    CORRADE_COMPILE_DATE),
                string.Format(CultureInfo.InvariantCulture,
                    CORRADE_CONSTANTS.COPYRIGHT + Environment.NewLine),
            };
            foreach (string line in logo)
            {
                Console.Write(line);
            }
            Console.WriteLine();
        }

        public static int Main(string[] args)
        {
            if (Environment.UserInteractive)
            {
                if (!args.Length.Equals(0))
                {
                    string action = string.Empty;
                    for (int i = 0; i < args.Length; ++i)
                    {
                        switch (args[i].ToUpper())
                        {
                            case "/INSTALL":
                                action = "INSTALL";
                                break;
                            case "/UNINSTALL":
                                action = "UNINSTALL";
                                break;
                            case "/NAME":
                                if (args.Length > i + 1)
                                {
                                    CorradeServiceName = args[++i];
                                }
                                break;
                        }
                    }
                    switch (action)
                    {
                        case "INSTALL":
                            return InstallService();
                        case "UNINSTALL":
                            return UninstallService();
                    }
                }
                // run interactively and log to console
                Corrade corrade = new Corrade();
                corrade.OnStart(null);
                return 0;
            }

            // run as a standard service
            Run(new Corrade());
            return 0;
        }

        private static int InstallService()
        {
            try
            {
                // install the service with the Windows Service Control Manager (SCM)
                ManagedInstallerClass.InstallHelper(new[] {Assembly.GetExecutingAssembly().Location});
            }
            catch (Exception e)
            {
                if (e.InnerException != null && e.InnerException.GetType() == typeof (Win32Exception))
                {
                    Win32Exception we = (Win32Exception) e.InnerException;
                    Console.WriteLine("Error(0x{0:X}): Service already installed!", we.ErrorCode);
                    return we.ErrorCode;
                }
                Console.WriteLine(e.ToString());
                return -1;
            }

            return 0;
        }

        private static int UninstallService()
        {
            try
            {
                // uninstall the service from the Windows Service Control Manager (SCM)
                ManagedInstallerClass.InstallHelper(new[] {"/u", Assembly.GetExecutingAssembly().Location});
            }
            catch (Exception e)
            {
                if (e.InnerException.GetType() == typeof (Win32Exception))
                {
                    Win32Exception we = (Win32Exception) e.InnerException;
                    Console.WriteLine("Error(0x{0:X}): Service not installed!", we.ErrorCode);
                    return we.ErrorCode;
                }
                Console.WriteLine(e.ToString());
                return -1;
            }

            return 0;
        }

        protected override void OnStop()
        {
            base.OnStop();
            ConnectionSemaphores.FirstOrDefault(o => o.Key.Equals('u')).Value.Set();
        }

        protected override void OnStart(string[] args)
        {
            base.OnStart(args);
            //Debugger.Break();
            programThread = new Thread(new Corrade().Program);
            programThread.Start();
        }

        // Main entry point.
        public void Program()
        {
            // Create a thread for signals.
            Thread BindSignalsThread = null;
            // Branch on platform and set-up termination handlers.
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    if (Environment.UserInteractive)
                    {
                        // Setup console handler.
                        ConsoleEventHandler += ConsoleCtrlCheck;
                        NativeMethods.SetConsoleCtrlHandler(ConsoleEventHandler, true);
                        if (Environment.UserInteractive)
                        {
                            Console.CancelKeyPress +=
                                (sender, args) =>
                                    ConnectionSemaphores.FirstOrDefault(o => o.Key.Equals('u')).Value.Set();
                        }
                    }
                    break;
                case PlatformID.Unix:
                    BindSignalsThread = new Thread(() =>
                    {
                        UnixSignal[] signals =
                        {
                            new UnixSignal(Signum.SIGTERM),
                            new UnixSignal(Signum.SIGINT)
                        };
                        UnixSignal.WaitAny(signals, -1);
                        ConnectionSemaphores.FirstOrDefault(o => o.Key.Equals('u')).Value.Set();
                    }) {IsBackground = true};
                    BindSignalsThread.Start();
                    break;
            }
            // Set the current directory to the service directory.
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            // Load the configuration file.
            Configuration.Load(CORRADE_CONSTANTS.CONFIGURATION_FILE);
            // Set-up watcher for dynamically reading the configuration file.
            FileSystemWatcher configurationWatcher = new FileSystemWatcher
            {
                Path = Directory.GetCurrentDirectory(),
                Filter = CORRADE_CONSTANTS.CONFIGURATION_FILE,
                NotifyFilter = NotifyFilters.LastWrite
            };
            configurationWatcher.Changed += HandleConfigurationFileChanged;
            configurationWatcher.EnableRaisingEvents = true;
            // Network Tweaks
            ServicePointManager.DefaultConnectionLimit = Configuration.CONNECTION_LIMIT;
            ServicePointManager.UseNagleAlgorithm = Configuration.USE_NAGGLE;
            ServicePointManager.Expect100Continue = Configuration.USE_EXPECT100CONTINUE;
            // Suppress standard OpenMetaverse logs, we have better ones.
            Settings.LOG_LEVEL = Helpers.LogLevel.None;
            Client.Settings.ALWAYS_REQUEST_PARCEL_ACL = true;
            Client.Settings.ALWAYS_DECODE_OBJECTS = true;
            Client.Settings.ALWAYS_REQUEST_OBJECTS = true;
            Client.Settings.SEND_AGENT_APPEARANCE = true;
            Client.Settings.AVATAR_TRACKING = true;
            Client.Settings.OBJECT_TRACKING = true;
            Client.Settings.PARCEL_TRACKING = true;
            Client.Settings.SEND_AGENT_UPDATES = true;
            Client.Settings.ENABLE_CAPS = true;
            Client.Settings.USE_ASSET_CACHE = true;
            Client.Settings.USE_INTERPOLATION_TIMER = true;
            Client.Settings.FETCH_MISSING_INVENTORY = true;
            Client.Settings.LOGIN_TIMEOUT = Configuration.SERVICES_TIMEOUT;
            Client.Settings.LOGOUT_TIMEOUT = Configuration.SERVICES_TIMEOUT;
            // Install global event handlers.
            Client.Inventory.InventoryObjectOffered += HandleInventoryObjectOffered;
            Client.Network.LoginProgress += HandleLoginProgress;
            Client.Network.SimConnected += HandleSimulatorConnected;
            Client.Network.Disconnected += HandleDisconnected;
            Client.Network.SimDisconnected += HandleSimulatorDisconnected;
            Client.Network.EventQueueRunning += HandleEventQueueRunning;
            Client.Friends.FriendshipOffered += HandleFriendshipOffered;
            Client.Friends.FriendshipResponse += HandleFriendShipResponse;
            Client.Friends.FriendOnline += HandleFriendOnlineStatus;
            Client.Friends.FriendOffline += HandleFriendOnlineStatus;
            Client.Friends.FriendRightsUpdate += HandleFriendRightsUpdate;
            Client.Self.TeleportProgress += HandleTeleportProgress;
            Client.Self.ScriptQuestion += HandleScriptQuestion;
            Client.Self.AlertMessage += HandleAlertMessage;
            Client.Self.MoneyBalance += HandleMoneyBalance;
            Client.Self.ChatFromSimulator += HandleChatFromSimulator;
            Client.Self.ScriptDialog += HandleScriptDialog;
            Client.Objects.AvatarUpdate += HandleAvatarUpdate;
            Client.Objects.TerseObjectUpdate += HandleTerseObjectUpdate;
            Client.Avatars.ViewerEffect += HandleViewerEffect;
            Client.Avatars.ViewerEffectPointAt += HandleViewerEffect;
            Client.Avatars.ViewerEffectLookAt += HandleViewerEffect;
            Client.Self.MeanCollision += HandleMeanCollision;
            Client.Self.RegionCrossed += HandleRegionCrossed;
            Client.Network.SimChanged += HandleSimChanged;
            Client.Self.MoneyBalanceReply += HandleMoneyBalance;
            // Each Instant Message is processed in its own thread.
            Client.Self.IM +=
                (sender, args) => new Thread(o => HandleSelfIM(sender, args)) {IsBackground = true}.Start();
            // Write the logo in interactive mode.
            if (Environment.UserInteractive)
            {
                WriteLogo();
            }
            // Check TOS
            if (!Configuration.TOS_ACCEPTED)
            {
                Feedback(wasGetDescriptionFromEnumValue(ConsoleError.TOS_NOT_ACCEPTED));
                Environment.Exit(1);
            }
            // Proceed to log-in.
            LoginParams login = new LoginParams(
                Client,
                Configuration.FIRST_NAME,
                Configuration.LAST_NAME,
                Configuration.PASSWORD,
                CORRADE_CONSTANTS.CLIENT_CHANNEL,
                CORRADE_VERSION.ToString(CultureInfo.InvariantCulture),
                Configuration.LOGIN_URL)
            {
                Author = @"Wizardry and Steamworks",
                AgreeToTos = Configuration.TOS_ACCEPTED,
                Start = Configuration.START_LOCATION,
                UserAgent = @"libopenmetaverse"
            };
            // Set the MAC if specified in the configuration file.
            if (!string.IsNullOrEmpty(Configuration.NETWORK_CARD_MAC))
            {
                login.MAC = Utils.MD5String(Configuration.NETWORK_CARD_MAC);
            }
            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.LOGGING_IN));
            Client.Network.Login(login);
            // Start the HTTP Server if it is supported
            Thread HTTPListenerThread = null;
            HttpListener HTTPListener = null;
            if (Configuration.ENABLE_HTTP_SERVER && !HttpListener.IsSupported)
            {
                Feedback(wasGetDescriptionFromEnumValue(ConsoleError.HTTP_SERVER_ERROR),
                    wasGetDescriptionFromEnumValue(ConsoleError.HTTP_SERVER_NOT_SUPPORTED));
            }
            if (Configuration.ENABLE_HTTP_SERVER && HttpListener.IsSupported)
            {
                Feedback(wasGetDescriptionFromEnumValue(ConsoleError.STARTING_HTTP_SERVER));
                HTTPListenerThread = new Thread(() =>
                {
                    try
                    {
                        using (HTTPListener = new HttpListener())
                        {
                            HTTPListener.Prefixes.Add(Configuration.HTTP_SERVER_PREFIX);
                            HTTPListener.Start();
                            while (HTTPListener.IsListening)
                            {
                                IAsyncResult result = HTTPListener.BeginGetContext(ProcesHTTPRequest, HTTPListener);
                                result.AsyncWaitHandle.WaitOne(Configuration.HTTP_SERVER_TIMEOUT, false);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Feedback(wasGetDescriptionFromEnumValue(ConsoleError.HTTP_SERVER_ERROR), e.Message);
                    }
                }) {IsBackground = true};
                HTTPListenerThread.Start();
            }
            // Start the callback thread to send callbacks.
            Thread CallbackThread = new Thread(() =>
            {
                while (runCallbackThread)
                {
                    if (!CallbackQueue.Count.Equals(0))
                    {
                        CallbackQueueElement callbackQueueElement;
                        lock (CallbackQueueLock)
                        {
                            callbackQueueElement = CallbackQueue.Dequeue();
                        }
                        try
                        {
                            if (!callbackQueueElement.Equals(default(CallbackQueueElement)))
                            {
                                wasPOST(callbackQueueElement.URL, callbackQueueElement.message,
                                    Configuration.CALLBACK_TIMEOUT);
                            }
                        }
                        catch (Exception e)
                        {
                            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.CALLBACK_ERROR),
                                callbackQueueElement.URL,
                                e.Message);
                        }
                    }
                    Thread.Sleep(Configuration.CALLBACK_THROTTLE);
                }
            }) {IsBackground = true};
            CallbackThread.Start();
            Thread NotificationThread = new Thread(() =>
            {
                while (runNotificationThread)
                {
                    if (!NotificationQueue.Count.Equals(0))
                    {
                        NotificationQueueElement notificationQueueElement;
                        lock (NotificationQueueLock)
                        {
                            notificationQueueElement = NotificationQueue.Dequeue();
                        }
                        try
                        {
                            if (!notificationQueueElement.Equals(default(NotificationQueueElement)))
                            {
                                wasPOST(notificationQueueElement.URL, notificationQueueElement.message,
                                    Configuration.NOTIFICATION_TIMEOUT);
                            }
                        }
                        catch (Exception e)
                        {
                            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.NOTIFICATION_ERROR),
                                notificationQueueElement.URL,
                                e.Message);
                        }
                    }
                    Thread.Sleep(Configuration.NOTIFICATION_THROTTLE);
                }
            }) {IsBackground = true};
            NotificationThread.Start();
            Thread GroupMemberSweepThread = new Thread(() =>
            {
                while (runGroupMemberSweepThread)
                {
                    Thread.Sleep(1);

                    if (!Client.Network.Connected || Configuration.GROUPS.Count.Equals(0)) continue;

                    // Request all the current groups.
                    HashSet<OpenMetaverse.Group> groups = new HashSet<OpenMetaverse.Group>();
                    if (!RequestCurrentGroups(Configuration.SERVICES_TIMEOUT, ref groups)) continue;

                    // Enqueue configured groups that are currently joined groups.
                    Queue<UUID> groupUUIDs = new Queue<UUID>();
                    foreach (Group group in Configuration.GROUPS)
                    {
                        UUID groupUUID = group.UUID;
                        if (groups.Any(o => o.ID.Equals(groupUUID)))
                        {
                            groupUUIDs.Enqueue(group.UUID);
                        }
                    }

                    // Bail if no configured groups are also joined.
                    if (groupUUIDs.Count.Equals(0)) continue;

                    // Get the last member count.
                    Queue<int> memberCount = new Queue<int>();
                    lock (GroupMembersLock)
                    {
                        foreach (KeyValuePair<UUID, HashSet<UUID>> groupMembers in
                            GroupMembers.SelectMany(groupMembers => groupUUIDs,
                                (groupMembers, groupUUID) => new {groupMembers, groupUUID})
                                .Where(o => o.groupUUID.Equals(o.groupMembers.Key))
                                .Select(p => p.groupMembers))
                        {
                            memberCount.Enqueue(groupMembers.Value.Count);
                        }
                    }

                    while (!groupUUIDs.Count.Equals(0) && runGroupMemberSweepThread)
                    {
                        UUID groupUUID = groupUUIDs.Dequeue();
                        // The total list of members.
                        HashSet<UUID> groupMembers = new HashSet<UUID>();
                        // New members that have joined the group.
                        HashSet<UUID> joinedMembers = new HashSet<UUID>();
                        // Members that have parted the group.
                        HashSet<UUID> partedMembers = new HashSet<UUID>();
                        ManualResetEvent GroupMembersReplyEvent = new ManualResetEvent(false);
                        EventHandler<GroupMembersReplyEventArgs> HandleGroupMembersReplyDelegate = (sender, args) =>
                        {
                            KeyValuePair<UUID, HashSet<UUID>> currentGroupMembers;
                            lock (GroupMembersLock)
                            {
                                currentGroupMembers =
                                    GroupMembers.FirstOrDefault(p => p.Key.Equals(args.GroupID));
                            }
                            if (!currentGroupMembers.Equals(default(KeyValuePair<UUID, HashSet<UUID>>)))
                            {
                                object LockObject = new object();
                                Parallel.ForEach(args.Members.Values, o =>
                                {
                                    if (!currentGroupMembers.Value.Contains(o.ID))
                                    {
                                        lock (LockObject)
                                        {
                                            joinedMembers.Add(o.ID);
                                        }
                                    }
                                });
                                Parallel.ForEach(currentGroupMembers.Value, o =>
                                {
                                    if (!args.Members.Values.Any(p => p.ID.Equals(o)))
                                    {
                                        lock (LockObject)
                                        {
                                            partedMembers.Add(o);
                                        }
                                    }
                                });
                            }
                            groupMembers = new HashSet<UUID>(args.Members.Values.Select(o => o.ID));
                            GroupMembersReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupMembersReply += HandleGroupMembersReplyDelegate;
                            Client.Groups.RequestGroupMembers(groupUUID);
                            GroupMembersReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false);
                            Client.Groups.GroupMembersReply -= HandleGroupMembersReplyDelegate;
                        }
                        lock (GroupMembersLock)
                        {
                            if (!GroupMembers.ContainsKey(groupUUID))
                            {
                                GroupMembers.Add(groupUUID, groupMembers);
                                continue;
                            }
                        }
                        if (!memberCount.Count.Equals(0))
                        {
                            if (!memberCount.Dequeue().Equals(groupMembers.Count))
                            {
                                if (!joinedMembers.Count.Equals(0))
                                {
                                    Parallel.ForEach(joinedMembers, o =>
                                    {
                                        string agentName = string.Empty;
                                        if (AgentUUIDToName(o, Configuration.SERVICES_TIMEOUT, ref agentName))
                                        {
                                            new Thread(
                                                p =>
                                                    SendNotification(Notifications.NOTIFICATION_GROUP_MEMBERSHIP,
                                                        new GroupMembershipEventArgs
                                                        {
                                                            AgentName = agentName,
                                                            AgentUUID = o,
                                                            Action = Action.JOINED
                                                        })) {IsBackground = true}
                                                .Start();
                                        }
                                    });
                                }
                                if (!partedMembers.Count.Equals(0))
                                {
                                    Parallel.ForEach(partedMembers, o =>
                                    {
                                        string agentName = string.Empty;
                                        if (AgentUUIDToName(o, Configuration.SERVICES_TIMEOUT, ref agentName))
                                        {
                                            new Thread(
                                                p =>
                                                    SendNotification(Notifications.NOTIFICATION_GROUP_MEMBERSHIP,
                                                        new GroupMembershipEventArgs
                                                        {
                                                            AgentName = agentName,
                                                            AgentUUID = o,
                                                            Action = Action.PARTED
                                                        })) {IsBackground = true}
                                                .Start();
                                        }
                                    });
                                }
                            }
                        }
                        lock (GroupMembersLock)
                        {
                            GroupMembers[groupUUID] = groupMembers;
                        }
                        Thread.Sleep(Configuration.MEMBERSHIP_SWEEP_INTERVAL);
                    }
                }
            }) {IsBackground = true};
            GroupMemberSweepThread.Start();
            // Load Corrade Caches
            LoadCorradeCache.Invoke();
            /*
             * The main thread spins around waiting for the semaphores to become invalidated,
             * at which point Corrade will consider its connection to the grid severed and
             * will terminate.
             *
             */
            WaitHandle.WaitAny(ConnectionSemaphores.Values.Select(o => (WaitHandle) o).ToArray());
            // Save Corrade Caches
            SaveCorradeCache.Invoke();
            // Now log-out.
            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.LOGGING_OUT));
            // Uninstall all installed handlers
            Client.Self.IM -= HandleSelfIM;
            Client.Self.MoneyBalanceReply -= HandleMoneyBalance;
            Client.Network.SimChanged -= HandleSimChanged;
            Client.Self.RegionCrossed -= HandleRegionCrossed;
            Client.Self.MeanCollision -= HandleMeanCollision;
            Client.Avatars.ViewerEffectLookAt -= HandleViewerEffect;
            Client.Avatars.ViewerEffectPointAt -= HandleViewerEffect;
            Client.Avatars.ViewerEffect -= HandleViewerEffect;
            Client.Objects.TerseObjectUpdate -= HandleTerseObjectUpdate;
            Client.Objects.AvatarUpdate -= HandleAvatarUpdate;
            Client.Self.ScriptDialog -= HandleScriptDialog;
            Client.Self.ChatFromSimulator -= HandleChatFromSimulator;
            Client.Self.MoneyBalance -= HandleMoneyBalance;
            Client.Self.AlertMessage -= HandleAlertMessage;
            Client.Self.ScriptQuestion -= HandleScriptQuestion;
            Client.Self.TeleportProgress -= HandleTeleportProgress;
            Client.Friends.FriendRightsUpdate -= HandleFriendRightsUpdate;
            Client.Friends.FriendOffline -= HandleFriendOnlineStatus;
            Client.Friends.FriendOnline -= HandleFriendOnlineStatus;
            Client.Friends.FriendshipResponse -= HandleFriendShipResponse;
            Client.Friends.FriendshipOffered -= HandleFriendshipOffered;
            Client.Network.EventQueueRunning -= HandleEventQueueRunning;
            Client.Network.SimDisconnected -= HandleSimulatorDisconnected;
            Client.Network.Disconnected -= HandleDisconnected;
            Client.Network.SimConnected -= HandleSimulatorConnected;
            Client.Network.LoginProgress -= HandleLoginProgress;
            Client.Inventory.InventoryObjectOffered -= HandleInventoryObjectOffered;
            // Stop the group sweep thread.
            runGroupMemberSweepThread = false;
            if (
                (GroupMemberSweepThread.ThreadState.Equals(ThreadState.Running) ||
                 GroupMemberSweepThread.ThreadState.Equals(ThreadState.WaitSleepJoin)))
            {
                if (!GroupMemberSweepThread.Join(1000))
                {
                    try
                    {
                        GroupMemberSweepThread.Abort();
                    }
                    catch (ThreadStateException)
                    {
                    }
                }
            }
            // Stop the notification thread.
            runNotificationThread = false;
            if (
                (NotificationThread.ThreadState.Equals(ThreadState.Running) ||
                 NotificationThread.ThreadState.Equals(ThreadState.WaitSleepJoin)))
            {
                if (!NotificationThread.Join(1000))
                {
                    try
                    {
                        NotificationThread.Abort();
                    }
                    catch (ThreadStateException)
                    {
                    }
                }
            }
            // Stop the callback thread.
            runCallbackThread = false;
            if (
                (CallbackThread.ThreadState.Equals(ThreadState.Running) ||
                 CallbackThread.ThreadState.Equals(ThreadState.WaitSleepJoin)))
            {
                if (!CallbackThread.Join(1000))
                {
                    try
                    {
                        CallbackThread.Abort();
                    }
                    catch (ThreadStateException)
                    {
                    }
                }
            }
            // Close HTTP server
            if (Configuration.ENABLE_HTTP_SERVER && HttpListener.IsSupported)
            {
                Feedback(wasGetDescriptionFromEnumValue(ConsoleError.STOPPING_HTTP_SERVER));
                if (HTTPListenerThread != null)
                {
                    HTTPListener.Stop();
                    if (
                        (HTTPListenerThread.ThreadState.Equals(ThreadState.Running) ||
                         HTTPListenerThread.ThreadState.Equals(ThreadState.WaitSleepJoin)))
                    {
                        if (!HTTPListenerThread.Join(1000))
                        {
                            try
                            {
                                HTTPListenerThread.Abort();
                            }
                            catch (ThreadStateException)
                            {
                            }
                        }
                    }
                }
            }
            // Reject any inventory that has not been accepted.
            lock (InventoryOffersLock)
            {
                Parallel.ForEach(InventoryOffers, o =>
                {
                    o.Key.Accept = false;
                    o.Value.Set();
                });
            }
            // Disable the watcher.
            configurationWatcher.EnableRaisingEvents = false;
            configurationWatcher.Dispose();
            // Logout
            if (Client.Network.Connected)
            {
                ManualResetEvent LoggedOutEvent = new ManualResetEvent(false);
                EventHandler<LoggedOutEventArgs> LoggedOutEventHandler = (sender, args) => LoggedOutEvent.Set();
                Client.Network.LoggedOut += LoggedOutEventHandler;
                Client.Network.RequestLogout();
                if (!LoggedOutEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                {
                    Client.Network.LoggedOut -= LoggedOutEventHandler;
                    Feedback(wasGetDescriptionFromEnumValue(ConsoleError.TIMEOUT_LOGGING_OUT));
                }
                Client.Network.LoggedOut -= LoggedOutEventHandler;
            }
            if (Client.Network.Connected)
            {
                Client.Network.Shutdown(NetworkManager.DisconnectType.ClientInitiated);
            }
            // Close signals
            if (Environment.OSVersion.Platform.Equals(PlatformID.Unix) && BindSignalsThread != null)
            {
                BindSignalsThread.Abort();
            }
            Environment.Exit(0);
        }

        private static void HandleRegionCrossed(object sender, RegionCrossedEventArgs e)
        {
            new Thread(o => SendNotification(Notifications.NOTIFICATION_REGION_CROSSED, e)) {IsBackground = true}.Start();
        }

        private static void HandleMeanCollision(object sender, MeanCollisionEventArgs e)
        {
            new Thread(o => SendNotification(Notifications.NOTIFICATION_MEAN_COLLISION, e)) {IsBackground = true}.Start();
        }

        private static void HandleViewerEffect(object sender, object e)
        {
            new Thread(o => SendNotification(Notifications.NOTIFICATION_VIEWER_EFFECT, e)) {IsBackground = true}.Start();
        }

        private static void ProcesHTTPRequest(IAsyncResult ar)
        {
            try
            {
                HttpListener httpListener = ar.AsyncState as HttpListener;
                // bail if we are not listening
                if (httpListener == null || !httpListener.IsListening) return;
                HttpListenerContext httpContext = httpListener.EndGetContext(ar);
                HttpListenerRequest httpRequest = httpContext.Request;
                // only accept POST requests
                if (!httpRequest.HttpMethod.Equals(WebRequestMethods.Http.Post, StringComparison.OrdinalIgnoreCase))
                    return;
                Stream body = httpRequest.InputStream;
                Encoding encoding = httpRequest.ContentEncoding;
                StreamReader reader = new StreamReader(body, encoding);
                Dictionary<string, string> result = HandleCorradeCommand(reader.ReadToEnd(),
                    CORRADE_CONSTANTS.WEB_REQUEST,
                    httpRequest.RemoteEndPoint.ToString());
                if (result == null) return;
                HttpListenerResponse response = httpContext.Response;
                response.ContentType = CORRADE_CONSTANTS.TEXT_HTML;
                byte[] data = Encoding.UTF8.GetBytes(wasKeyValueEncode(wasKeyValueEscape(result)));
                response.ContentLength64 = data.Length;
                response.StatusCode = CORRADE_CONSTANTS.HTTP_CODES.OK; // HTTP OK
                Stream responseStream = response.OutputStream;
                if (responseStream == null) return;
                responseStream.Write(data, 0, data.Length);
                responseStream.Close();
            }
            catch (Exception)
            {
                Feedback(wasGetDescriptionFromEnumValue(ConsoleError.HTTP_SERVER_PROCESSING_ABORTED));
            }
        }

        private static void SendNotification(Notifications notification, object args)
        {
            // Only send notifications for groups that have bound to the notification to send.
            lock (GroupNotificationsLock)
            {
                Parallel.ForEach(GroupNotifications, o =>
                {
                    if (!HasCorradeNotification(o.GROUP, (uint) notification) ||
                        (o.NOTIFICATION_MASK & (uint) notification).Equals(0)) return;
                    // Set the notification type
                    Dictionary<string, string> notificationData = new Dictionary<string, string>
                    {
                        {
                            wasGetDescriptionFromEnumValue(ScriptKeys.TYPE),
                            wasGetDescriptionFromEnumValue(notification)
                        }
                    };
                    switch (notification)
                    {
                        case Notifications.NOTIFICATION_SCRIPT_DIALOG:
                            ScriptDialogEventArgs scriptDialogEventArgs = (ScriptDialogEventArgs) args;
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                scriptDialogEventArgs.Message);
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                scriptDialogEventArgs.FirstName);
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                scriptDialogEventArgs.LastName);
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.CHANNEL),
                                scriptDialogEventArgs.Channel.ToString(CultureInfo.InvariantCulture));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.NAME),
                                scriptDialogEventArgs.ObjectName);
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ITEM),
                                scriptDialogEventArgs.ObjectID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.OWNER),
                                scriptDialogEventArgs.OwnerID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.BUTTON),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    scriptDialogEventArgs.ButtonLabels.ToArray()));
                            break;
                        case Notifications.NOTIFICATION_LOCAL_CHAT:
                            ChatEventArgs chatEventArgs = (ChatEventArgs) args;
                            List<string> chatName =
                                new List<string>(GetAvatarNames(chatEventArgs.FromName));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                chatEventArgs.Message);
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME), chatName.First());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME), chatName.Last());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.OWNER),
                                chatEventArgs.OwnerID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ITEM),
                                chatEventArgs.SourceID.ToString());
                            break;
                        case Notifications.NOTIFICATION_BALANCE:
                            BalanceEventArgs balanceEventArgs = (BalanceEventArgs) args;
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.BALANCE),
                                balanceEventArgs.Balance.ToString(CultureInfo.InvariantCulture));
                            break;
                        case Notifications.NOTIFICATION_ALERT_MESSAGE:
                            AlertMessageEventArgs alertMessageEventArgs = (AlertMessageEventArgs) args;
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                alertMessageEventArgs.Message);
                            break;
                        case Notifications.NOTIFICATION_INVENTORY:
                            System.Type inventoryOfferedType = args.GetType();
                            if (inventoryOfferedType == typeof (InstantMessageEventArgs))
                            {
                                InstantMessageEventArgs inventoryOfferEventArgs = (InstantMessageEventArgs) args;
                                List<string> inventoryOfferName =
                                    new List<string>(
                                        GetAvatarNames(inventoryOfferEventArgs.IM.FromAgentName));
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                    inventoryOfferName.First());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                    inventoryOfferName.Last());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                    inventoryOfferEventArgs.IM.FromAgentID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                    inventoryOfferEventArgs.IM.Dialog == InstantMessageDialog.InventoryAccepted
                                        ? wasGetDescriptionFromEnumValue(Action.ACCEPT)
                                        : wasGetDescriptionFromEnumValue(Action.DECLINE));
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                    wasGetDescriptionFromEnumValue(Action.REPLY));
                                break;
                            }
                            if (inventoryOfferedType == typeof (InventoryObjectOfferedEventArgs))
                            {
                                InventoryObjectOfferedEventArgs inventoryObjectOfferedEventArgs =
                                    (InventoryObjectOfferedEventArgs) args;
                                List<string> inventoryObjectOfferedName =
                                    new List<string>(
                                        GetAvatarNames(inventoryObjectOfferedEventArgs.Offer.FromAgentName));
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                    inventoryObjectOfferedName.First());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                    inventoryObjectOfferedName.Last());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                    inventoryObjectOfferedEventArgs.Offer.FromAgentID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ASSET),
                                    inventoryObjectOfferedEventArgs.AssetType.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.NAME),
                                    inventoryObjectOfferedEventArgs.Offer.Message);
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.SESSION),
                                    inventoryObjectOfferedEventArgs.Offer.IMSessionID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                    wasGetDescriptionFromEnumValue(Action.OFFER));
                            }
                            break;
                        case Notifications.NOTIFICATION_SCRIPT_PERMISSION:
                            ScriptQuestionEventArgs scriptQuestionEventArgs = (ScriptQuestionEventArgs) args;
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ITEM),
                                scriptQuestionEventArgs.ItemID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.TASK),
                                scriptQuestionEventArgs.TaskID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.PERMISSIONS),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    typeof (ScriptPermission).GetFields(BindingFlags.Public |
                                                                        BindingFlags.Static)
                                        .Where(
                                            p =>
                                                !(((int) p.GetValue(null) &
                                                   (int) scriptQuestionEventArgs.Questions)).Equals(0))
                                        .Select(p => p.Name).ToArray()));
                            break;
                        case Notifications.NOTIFICATION_FRIENDSHIP:
                            System.Type friendshipNotificationType = args.GetType();
                            if (friendshipNotificationType == typeof (FriendInfoEventArgs))
                            {
                                FriendInfoEventArgs friendInfoEventArgs = (FriendInfoEventArgs) args;
                                List<string> name =
                                    new List<string>(GetAvatarNames(friendInfoEventArgs.Friend.Name));
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME), name.First());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME), name.Last());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                    friendInfoEventArgs.Friend.UUID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.STATUS),
                                    friendInfoEventArgs.Friend.IsOnline
                                        ? wasGetDescriptionFromEnumValue(Action.ONLINE)
                                        : wasGetDescriptionFromEnumValue(Action.OFFLINE));
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.RIGHTS),
                                    // Return the friend rights as a nice CSV string.
                                    string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                        typeof (FriendRights).GetFields(BindingFlags.Public |
                                                                        BindingFlags.Static)
                                            .Where(
                                                p =>
                                                    !(((int) p.GetValue(null) &
                                                       (int) friendInfoEventArgs.Friend.MyFriendRights))
                                                        .Equals(
                                                            0))
                                            .Select(p => p.Name)
                                            .ToArray()));
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                    wasGetDescriptionFromEnumValue(Action.UPDATE));
                                break;
                            }
                            if (friendshipNotificationType == typeof (FriendshipResponseEventArgs))
                            {
                                FriendshipResponseEventArgs friendshipResponseEventArgs =
                                    (FriendshipResponseEventArgs) args;
                                List<string> friendshipResponseName =
                                    new List<string>(
                                        GetAvatarNames(friendshipResponseEventArgs.AgentName));
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                    friendshipResponseName.First());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                    friendshipResponseName.Last());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                    friendshipResponseEventArgs.AgentID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                    wasGetDescriptionFromEnumValue(Action.RESPONSE));
                                break;
                            }
                            if (friendshipNotificationType == typeof (FriendshipOfferedEventArgs))
                            {
                                FriendshipOfferedEventArgs friendshipOfferedEventArgs =
                                    (FriendshipOfferedEventArgs) args;
                                List<string> friendshipOfferedName =
                                    new List<string>(GetAvatarNames(friendshipOfferedEventArgs.AgentName));
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                    friendshipOfferedName.First());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                    friendshipOfferedName.Last());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                    friendshipOfferedEventArgs.AgentID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                    wasGetDescriptionFromEnumValue(Action.REQUEST));
                            }
                            break;
                        case Notifications.NOTIFICATION_TELEPORT_LURE:
                            InstantMessageEventArgs teleportLureEventArgs = (InstantMessageEventArgs) args;
                            List<string> teleportLureName =
                                new List<string>(
                                    GetAvatarNames(teleportLureEventArgs.IM.FromAgentName));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                teleportLureName.First());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                teleportLureName.Last());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                teleportLureEventArgs.IM.FromAgentID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.SESSION),
                                teleportLureEventArgs.IM.IMSessionID.ToString());
                            break;
                        case Notifications.NOTIFICATION_GROUP_NOTICE:
                            InstantMessageEventArgs notificationGroupNoticeEventArgs =
                                (InstantMessageEventArgs) args;
                            List<string> notificationGroupNoticeName =
                                new List<string>(
                                    GetAvatarNames(notificationGroupNoticeEventArgs.IM.FromAgentName));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                notificationGroupNoticeName.First());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                notificationGroupNoticeName.Last());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                notificationGroupNoticeEventArgs.IM.FromAgentID.ToString());
                            string[] noticeData = notificationGroupNoticeEventArgs.IM.Message.Split('|');
                            if (noticeData.Length > 0 && !string.IsNullOrEmpty(noticeData[0]))
                            {
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.SUBJECT), noticeData[0]);
                            }
                            if (noticeData.Length > 1 && !string.IsNullOrEmpty(noticeData[1]))
                            {
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE), noticeData[1]);
                            }
                            switch (notificationGroupNoticeEventArgs.IM.Dialog)
                            {
                                case InstantMessageDialog.GroupNoticeInventoryAccepted:
                                case InstantMessageDialog.GroupNoticeInventoryDeclined:
                                    notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        !notificationGroupNoticeEventArgs.IM.Dialog.Equals(
                                            InstantMessageDialog.GroupNoticeInventoryAccepted)
                                            ? wasGetDescriptionFromEnumValue(Action.DECLINE)
                                            : wasGetDescriptionFromEnumValue(Action.ACCEPT));
                                    break;
                                case InstantMessageDialog.GroupNotice:
                                    notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        wasGetDescriptionFromEnumValue(Action.RECEIVED));
                                    break;
                            }
                            break;
                        case Notifications.NOTIFICATION_INSTANT_MESSAGE:
                            InstantMessageEventArgs notificationInstantMessage =
                                (InstantMessageEventArgs) args;
                            List<string> notificationInstantMessageName =
                                new List<string>(
                                    GetAvatarNames(notificationInstantMessage.IM.FromAgentName));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                notificationInstantMessageName.First());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                notificationInstantMessageName.Last());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                notificationInstantMessage.IM.FromAgentID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                notificationInstantMessage.IM.Message);
                            break;
                        case Notifications.NOTIFICATION_REGION_MESSAGE:
                            InstantMessageEventArgs notificationRegionMessage =
                                (InstantMessageEventArgs) args;
                            List<string> notificationRegionMessageName =
                                new List<string>(
                                    GetAvatarNames(notificationRegionMessage.IM.FromAgentName));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                notificationRegionMessageName.First());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                notificationRegionMessageName.Last());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                notificationRegionMessage.IM.FromAgentID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                notificationRegionMessage.IM.Message);
                            break;
                        case Notifications.NOTIFICATION_GROUP_MESSAGE:
                            InstantMessageEventArgs notificationGroupMessage =
                                (InstantMessageEventArgs) args;
                            List<string> notificationGroupMessageName =
                                new List<string>(
                                    GetAvatarNames(notificationGroupMessage.IM.FromAgentName));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                notificationGroupMessageName.First());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                notificationGroupMessageName.Last());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                notificationGroupMessage.IM.FromAgentID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.GROUP), o.GROUP);
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                notificationGroupMessage.IM.Message);
                            break;
                        case Notifications.NOTIFICATION_VIEWER_EFFECT:
                            System.Type viewerEffectType = args.GetType();
                            if (viewerEffectType == typeof (ViewerEffectEventArgs))
                            {
                                ViewerEffectEventArgs notificationViewerEffectEventArgs =
                                    (ViewerEffectEventArgs) args;
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.EFFECT),
                                    notificationViewerEffectEventArgs.Type.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.SOURCE),
                                    notificationViewerEffectEventArgs.SourceID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.TARGET),
                                    notificationViewerEffectEventArgs.TargetID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION),
                                    notificationViewerEffectEventArgs.TargetPosition.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.DURATION),
                                    notificationViewerEffectEventArgs.Duration.ToString(
                                        CultureInfo.InvariantCulture));
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ID),
                                    notificationViewerEffectEventArgs.EffectID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                    wasGetDescriptionFromEnumValue(Action.GENERIC));
                                break;
                            }
                            if (viewerEffectType == typeof (ViewerEffectPointAtEventArgs))
                            {
                                ViewerEffectPointAtEventArgs notificationViewerPointAtEventArgs =
                                    (ViewerEffectPointAtEventArgs) args;
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.SOURCE),
                                    notificationViewerPointAtEventArgs.SourceID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.TARGET),
                                    notificationViewerPointAtEventArgs.TargetID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION),
                                    notificationViewerPointAtEventArgs.TargetPosition.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.DURATION),
                                    notificationViewerPointAtEventArgs.Duration.ToString(
                                        CultureInfo.InvariantCulture));
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ID),
                                    notificationViewerPointAtEventArgs.EffectID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                    wasGetDescriptionFromEnumValue(Action.POINT));
                                break;
                            }
                            if (viewerEffectType == typeof (ViewerEffectLookAtEventArgs))
                            {
                                ViewerEffectLookAtEventArgs notificationViewerLookAtEventArgs =
                                    (ViewerEffectLookAtEventArgs) args;
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.SOURCE),
                                    notificationViewerLookAtEventArgs.SourceID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.TARGET),
                                    notificationViewerLookAtEventArgs.TargetID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION),
                                    notificationViewerLookAtEventArgs.TargetPosition.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.DURATION),
                                    notificationViewerLookAtEventArgs.Duration.ToString(
                                        CultureInfo.InvariantCulture));
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ID),
                                    notificationViewerLookAtEventArgs.EffectID.ToString());
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                    wasGetDescriptionFromEnumValue(Action.LOOK));
                            }
                            break;
                        case Notifications.NOTIFICATION_MEAN_COLLISION:
                            MeanCollisionEventArgs meanCollisionEventArgs =
                                (MeanCollisionEventArgs) args;
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGGRESSOR),
                                meanCollisionEventArgs.Aggressor.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.MAGNITUDE),
                                meanCollisionEventArgs.Magnitude.ToString(CultureInfo.InvariantCulture));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.TIME),
                                meanCollisionEventArgs.Time.ToLongDateString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ENTITY),
                                meanCollisionEventArgs.Type.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.VICTIM),
                                meanCollisionEventArgs.Victim.ToString());
                            break;
                        case Notifications.NOTIFICATION_REGION_CROSSED:
                            System.Type regionChangeType = args.GetType();
                            if (regionChangeType == typeof (SimChangedEventArgs))
                            {
                                SimChangedEventArgs simChangedEventArgs = (SimChangedEventArgs) args;
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.OLD),
                                    simChangedEventArgs.PreviousSimulator.Name);
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.NEW),
                                    Client.Network.CurrentSim.Name);
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                    wasGetDescriptionFromEnumValue(Action.CHANGED));
                                break;
                            }
                            if (regionChangeType == typeof (RegionCrossedEventArgs))
                            {
                                RegionCrossedEventArgs regionCrossedEventArgs =
                                    (RegionCrossedEventArgs) args;
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.OLD),
                                    regionCrossedEventArgs.OldSimulator.Name);
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.NEW),
                                    regionCrossedEventArgs.NewSimulator.Name);
                                notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                    wasGetDescriptionFromEnumValue(Action.CROSSED));
                            }
                            break;
                        case Notifications.NOTIFICATION_TERSE_UPDATES:
                            TerseObjectUpdateEventArgs terseObjectUpdateEventArgs =
                                (TerseObjectUpdateEventArgs) args;
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ID),
                                terseObjectUpdateEventArgs.Prim.ID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION),
                                terseObjectUpdateEventArgs.Prim.Position.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ROTATION),
                                terseObjectUpdateEventArgs.Prim.Rotation.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ENTITY),
                                terseObjectUpdateEventArgs.Prim.PrimData.PCode.ToString());
                            break;
                        case Notifications.NOTIFICATION_TYPING:
                            InstantMessageEventArgs notificationTypingMessageEventArgs = (InstantMessageEventArgs) args;
                            List<string> notificationTypingMessageName =
                                new List<string>(
                                    GetAvatarNames(notificationTypingMessageEventArgs.IM.FromAgentName));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                notificationTypingMessageName.First());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                notificationTypingMessageName.Last());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                notificationTypingMessageEventArgs.IM.FromAgentID.ToString());
                            switch (notificationTypingMessageEventArgs.IM.Dialog)
                            {
                                case InstantMessageDialog.StartTyping:
                                case InstantMessageDialog.StopTyping:
                                    notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        !notificationTypingMessageEventArgs.IM.Dialog.Equals(
                                            InstantMessageDialog.StartTyping)
                                            ? wasGetDescriptionFromEnumValue(Action.STOP)
                                            : wasGetDescriptionFromEnumValue(Action.START));
                                    break;
                            }
                            break;
                        case Notifications.NOTIFICATION_GROUP_INVITE:
                            InstantMessageEventArgs notificationGroupInviteEventArgs = (InstantMessageEventArgs) args;
                            List<string> notificationGroupInviteName =
                                new List<string>(
                                    GetAvatarNames(notificationGroupInviteEventArgs.IM.FromAgentName));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                notificationGroupInviteName.First());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                notificationGroupInviteName.Last());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                notificationGroupInviteEventArgs.IM.FromAgentID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.GROUP),
                                GroupInvites.FirstOrDefault(
                                    p => p.Session.Equals(notificationGroupInviteEventArgs.IM.IMSessionID))
                                    .Group);
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.SESSION),
                                notificationGroupInviteEventArgs.IM.IMSessionID.ToString());
                            break;
                        case Notifications.NOTIFICATION_ECONOMY:
                            MoneyBalanceReplyEventArgs notificationMoneyBalanceEventArgs =
                                (MoneyBalanceReplyEventArgs) args;
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.BALANCE),
                                notificationMoneyBalanceEventArgs.Balance.ToString(CultureInfo.InvariantCulture));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.DESCRIPTION),
                                notificationMoneyBalanceEventArgs.Description);
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.COMMITTED),
                                notificationMoneyBalanceEventArgs.MetersCommitted.ToString(CultureInfo.InvariantCulture));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.CREDIT),
                                notificationMoneyBalanceEventArgs.MetersCredit.ToString(CultureInfo.InvariantCulture));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.SUCCESS),
                                notificationMoneyBalanceEventArgs.Success.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ID),
                                notificationMoneyBalanceEventArgs.TransactionID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AMOUNT),
                                notificationMoneyBalanceEventArgs.TransactionInfo.Amount.ToString(
                                    CultureInfo.InvariantCulture));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.TARGET),
                                notificationMoneyBalanceEventArgs.TransactionInfo.DestID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.SOURCE),
                                notificationMoneyBalanceEventArgs.TransactionInfo.SourceID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.TRANSACTION),
                                Enum.GetName(typeof (MoneyTransactionType),
                                    notificationMoneyBalanceEventArgs.TransactionInfo.TransactionType));
                            break;
                        case Notifications.NOTIFICATION_GROUP_MEMBERSHIP:
                            GroupMembershipEventArgs groupMembershipEventArgs = (GroupMembershipEventArgs) args;
                            List<string> groupMembershipName =
                                new List<string>(
                                    GetAvatarNames(groupMembershipEventArgs.AgentName));
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                groupMembershipName.First());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                groupMembershipName.Last());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                groupMembershipEventArgs.AgentUUID.ToString());
                            notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.GROUP),
                                o.GROUP);
                            switch (groupMembershipEventArgs.Action)
                            {
                                case Action.JOINED:
                                case Action.PARTED:
                                    notificationData.Add(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        !groupMembershipEventArgs.Action.Equals(
                                            Action.JOINED)
                                            ? wasGetDescriptionFromEnumValue(Action.PARTED)
                                            : wasGetDescriptionFromEnumValue(Action.JOINED));
                                    break;
                            }
                            break;
                    }
                    if (NotificationQueue.Count < Configuration.NOTIFICATION_QUEUE_LENGTH)
                    {
                        lock (NotificationQueueLock)
                        {
                            NotificationQueue.Enqueue(new NotificationQueueElement
                            {
                                URL = o.URL,
                                message = wasKeyValueEscape(notificationData)
                            });
                        }
                    }
                });
            }
        }

        private static void HandleScriptDialog(object sender, ScriptDialogEventArgs e)
        {
            lock (ScriptDialogLock)
            {
                ScriptDialogs.Add(new ScriptDialog
                {
                    Message = e.Message,
                    Agent = new Agent
                    {
                        FirstName = e.FirstName,
                        LastName = e.LastName,
                        UUID = e.OwnerID
                    },
                    Channel = e.Channel,
                    Name = e.ObjectName,
                    Item = e.ObjectID,
                    Button = e.ButtonLabels
                });
            }
            new Thread(o => SendNotification(Notifications.NOTIFICATION_SCRIPT_DIALOG, e)) {IsBackground = true}.Start();
        }

        private static void HandleChatFromSimulator(object sender, ChatEventArgs e)
        {
            // Ignore self
            if (e.SourceID.Equals(Client.Self.AgentID)) return;
            // Ignore chat with no message (ie: start / stop typing)
            if (string.IsNullOrEmpty(e.Message)) return;
            switch (e.Type)
            {
                case ChatType.OwnerSay:
                    new Thread(() =>
                    {
                        if (!EnableCorradeRLV) return;
                        if (!e.Message.StartsWith(RLV_CONSTANTS.COMMAND_OPERATOR)) return;
                        HandleRLVCommand(e.Message.Substring(1, e.Message.Length - 1), e.SourceID);
                    }) {IsBackground = true}.Start();
                    break;
                case ChatType.Debug:
                case ChatType.Normal:
                case ChatType.Shout:
                case ChatType.Whisper:
                    // Send chat notifications.
                    new Thread(() => SendNotification(Notifications.NOTIFICATION_LOCAL_CHAT, e)) {IsBackground = true}
                        .Start();
                    break;
                case (ChatType) 9:
                    new Thread(() => HandleCorradeCommand(e.Message, e.FromName, e.OwnerID.ToString()))
                    {
                        IsBackground = true
                    }.Start();
                    break;
            }
        }

        private static void HandleMoneyBalance(object sender, BalanceEventArgs e)
        {
            new Thread(o => SendNotification(Notifications.NOTIFICATION_BALANCE, e)) {IsBackground = true}.Start();
        }

        private static void HandleAlertMessage(object sender, AlertMessageEventArgs e)
        {
            new Thread(o => SendNotification(Notifications.NOTIFICATION_ALERT_MESSAGE, e)) {IsBackground = true}.Start();
        }

        private static void HandleInventoryObjectOffered(object sender, InventoryObjectOfferedEventArgs e)
        {
            // Accept anything from master avatars.
            if (
                Configuration.MASTERS.Select(
                    o => string.Format(CultureInfo.InvariantCulture, "{0} {1}", o.FirstName, o.LastName))
                    .Any(p => p.Equals(e.Offer.FromAgentName, StringComparison.OrdinalIgnoreCase)))
            {
                e.Accept = true;
                return;
            }

            // We need to block until we get a reply from a script.
            ManualResetEvent wait = new ManualResetEvent(false);
            // Add the inventory offer to the list of inventory items.
            lock (InventoryOffersLock)
            {
                InventoryOffers.Add(e, wait);
            }

            // Find the item in the inventory.
            InventoryBase inventoryBaseItem =
                FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, ((Func<string>) (() =>
                {
                    GroupCollection groups = Regex.Match(e.Offer.Message, @"'{0,1}(.+)'{0,1}").Groups;
                    return groups.Count >= 1 ? groups[1].Value : string.Empty;
                }))()
                    ).FirstOrDefault();

            if (inventoryBaseItem != null)
            {
                // Assume we do not want the item.
                Client.Inventory.Move(
                    inventoryBaseItem,
                    (InventoryFolder)
                        Client.Inventory.Store.Items[Client.Inventory.FindFolderForType(AssetType.TrashFolder)].Data);
            }

            // Send notification
            new Thread(o => SendNotification(Notifications.NOTIFICATION_INVENTORY, e)) {IsBackground = true}.Start();
            // Wait for a reply.
            wait.WaitOne(Timeout.Infinite);

            if (!e.Accept) return;

            // If no folder UUID was specified, move it to the default folder for the asset type.
            if (inventoryBaseItem != null)
            {
                if (e.FolderID.Equals(UUID.Zero))
                {
                    Client.Inventory.Move(
                        inventoryBaseItem,
                        (InventoryFolder)
                            Client.Inventory.Store.Items[Client.Inventory.FindFolderForType(e.AssetType)].Data);
                    return;
                }
                // Otherwise, locate the folder and move.
                InventoryBase inventoryBaseFolder =
                    FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, e.FolderID
                        ).FirstOrDefault();
                if (inventoryBaseFolder != null)
                {
                    Client.Inventory.Move(inventoryBaseItem, inventoryBaseFolder as InventoryFolder);
                }
            }

            lock (InventoryOffersLock)
            {
                InventoryOffers.Remove(e);
            }
        }

        private static void HandleScriptQuestion(object sender, ScriptQuestionEventArgs e)
        {
            List<string> owner = new List<string>(GetAvatarNames(e.ObjectOwnerName));
            UUID ownerUUID = UUID.Zero;
            // Don't add permission requests from unknown agents.
            if (!AgentNameToUUID(owner.First(), owner.Last(), Configuration.SERVICES_TIMEOUT, ref ownerUUID))
            {
                return;
            }
            // Handle RLV: acceptpermission
            lock (RLVRuleLock)
            {
                if (RLVRules.Any(o => o.Behaviour.Equals(wasGetDescriptionFromEnumValue(RLVBehaviour.ACCEPTPERMISSION))))
                {
                    Client.Self.ScriptQuestionReply(Client.Network.CurrentSim, e.ItemID, e.TaskID, e.Questions);
                    return;
                }
            }
            lock (ScriptPermissionRequestLock)
            {
                ScriptPermissionRequests.Add(new ScriptPermissionRequest
                {
                    Name = e.ObjectName,
                    Agent = new Agent
                    {
                        FirstName = owner.First(),
                        LastName = owner.Last(),
                        UUID = ownerUUID
                    },
                    Item = e.ItemID,
                    Task = e.TaskID,
                    Permission = e.Questions
                });
            }
            new Thread(o => SendNotification(Notifications.NOTIFICATION_SCRIPT_PERMISSION, e)) {IsBackground = true}
                .Start();
        }

        private static void HandleConfigurationFileChanged(object sender, FileSystemEventArgs e)
        {
            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.CONFIGURATION_FILE_MODIFIED));
            Configuration.Load(e.Name);
        }

        private static void HandleDisconnected(object sender, DisconnectedEventArgs e)
        {
            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.DISCONNECTED));
            ConnectionSemaphores.FirstOrDefault(o => o.Key.Equals('l')).Value.Set();
        }

        private static void HandleEventQueueRunning(object sender, EventQueueRunningEventArgs e)
        {
            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.EVENT_QUEUE_STARTED));
        }

        private static void HandleSimulatorConnected(object sender, SimConnectedEventArgs e)
        {
            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.SIMULATOR_CONNECTED));
        }

        private static void HandleSimulatorDisconnected(object sender, SimDisconnectedEventArgs e)
        {
            // if any simulators are still connected, we are not disconnected
            if (Client.Network.Simulators.Any())
                return;
            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.ALL_SIMULATORS_DISCONNECTED));
            ConnectionSemaphores.FirstOrDefault(o => o.Key.Equals('s')).Value.Set();
        }

        private static void HandleLoginProgress(object sender, LoginProgressEventArgs e)
        {
            switch (e.Status)
            {
                case LoginStatus.Success:
                    Feedback(wasGetDescriptionFromEnumValue(ConsoleError.LOGIN_SUCCEEDED));
                    // Set current group to land group.
                    new Thread(() =>
                    {
                        if (Configuration.AUTO_ACTIVATE_GROUP)
                        {
                            if (!ActivateCurrentLandGroup(Configuration.SERVICES_TIMEOUT, Configuration.ACTIVATE_DELAY))
                            {
                                Feedback(wasGetDescriptionFromEnumValue(ConsoleError.FAILED_TO_ACTIVATE_LAND_GROUP));
                            }
                        }
                    }) {IsBackground = true}.Start();
                    // Start the inventory update thread.
                    new Thread(() =>
                    {
                        lock (InventoryLock)
                        {
                            LoadInventoryCache.Invoke();
                            InventoryUpdate.Invoke();
                            SaveInventoryCache.Invoke();
                        }
                    }) {IsBackground = true}.Start();
                    break;
                case LoginStatus.Failed:
                    Feedback(wasGetDescriptionFromEnumValue(ConsoleError.LOGIN_FAILED), e.FailReason);
                    ConnectionSemaphores.FirstOrDefault(o => o.Key.Equals('l')).Value.Set();
                    break;
            }
        }

        private static void HandleFriendOnlineStatus(object sender, FriendInfoEventArgs e)
        {
            new Thread(o => SendNotification(Notifications.NOTIFICATION_FRIENDSHIP, e)) {IsBackground = true}.Start();
        }

        private static void HandleFriendRightsUpdate(object sender, FriendInfoEventArgs e)
        {
            new Thread(o => SendNotification(Notifications.NOTIFICATION_FRIENDSHIP, e)) {IsBackground = true}.Start();
        }

        private static void HandleFriendShipResponse(object sender, FriendshipResponseEventArgs e)
        {
            new Thread(o => SendNotification(Notifications.NOTIFICATION_FRIENDSHIP, e)) {IsBackground = true}.Start();
        }

        private static void HandleFriendshipOffered(object sender, FriendshipOfferedEventArgs e)
        {
            // Send friendship notifications
            new Thread(o => SendNotification(Notifications.NOTIFICATION_FRIENDSHIP, e)) {IsBackground = true}.Start();
            // Accept friendships only from masters (for the time being)
            if (
                !Configuration.MASTERS.Select(
                    o => string.Format(CultureInfo.InvariantCulture, "{0} {1}", o.FirstName, o.LastName))
                    .Any(p => p.Equals(e.AgentName, StringComparison.CurrentCultureIgnoreCase)))
                return;
            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.ACCEPTED_FRIENDSHIP), e.AgentName);
            Client.Friends.AcceptFriendship(e.AgentID, e.SessionID);
        }

        private static void HandleTeleportProgress(object sender, TeleportEventArgs e)
        {
            switch (e.Status)
            {
                case TeleportStatus.Finished:
                    Feedback(wasGetDescriptionFromEnumValue(ConsoleError.TELEPORT_SUCCEEDED));
                    // Set current group to land group.
                    new Thread(() =>
                    {
                        if (Configuration.AUTO_ACTIVATE_GROUP)
                        {
                            if (!ActivateCurrentLandGroup(Configuration.SERVICES_TIMEOUT, Configuration.ACTIVATE_DELAY))
                            {
                                Feedback(wasGetDescriptionFromEnumValue(ConsoleError.FAILED_TO_ACTIVATE_LAND_GROUP));
                            }
                        }
                    }) {IsBackground = true}.Start();
                    break;
                case TeleportStatus.Failed:
                    Feedback(wasGetDescriptionFromEnumValue(ConsoleError.TELEPORT_FAILED));
                    break;
            }
        }

        private static void HandleSelfIM(object sender, InstantMessageEventArgs args)
        {
            // Ignore self.
            if (args.IM.FromAgentName.Equals(string.Join(" ", new[] {Client.Self.FirstName, Client.Self.LastName}),
                StringComparison.Ordinal))
                return;
            // Create a copy of the message.
            string message = args.IM.Message;
            // Process dialog messages.
            switch (args.IM.Dialog)
            {
                    // Send typing notification.
                case InstantMessageDialog.StartTyping:
                case InstantMessageDialog.StopTyping:
                    new Thread(o => SendNotification(Notifications.NOTIFICATION_TYPING, args)) {IsBackground = true}
                        .Start();
                    return;
                case InstantMessageDialog.InventoryAccepted:
                case InstantMessageDialog.InventoryDeclined:
                case InstantMessageDialog.TaskInventoryOffered:
                case InstantMessageDialog.InventoryOffered:
                    new Thread(o => SendNotification(Notifications.NOTIFICATION_INVENTORY, args)) {IsBackground = true}
                        .Start();
                    return;
                case InstantMessageDialog.MessageBox:
                    // Not used.
                    return;
                case InstantMessageDialog.RequestTeleport:
                    // Handle RLV: acccepttp
                    lock (RLVRuleLock)
                    {
                        if (RLVRules.Any(o => o.Behaviour.Equals(wasGetDescriptionFromEnumValue(RLVBehaviour.ACCEPTTP))))
                        {
                            lock (TeleportLock)
                            {
                                Client.Self.TeleportLureRespond(args.IM.FromAgentID, args.IM.IMSessionID, true);
                            }
                            return;
                        }
                    }
                    // Handle Corrade
                    List<string> teleportLureName =
                        new List<string>(
                            GetAvatarNames(args.IM.FromAgentName));
                    // Store teleport lure.
                    lock (TeleportLureLock)
                    {
                        TeleportLures.Add(new TeleportLure
                        {
                            Agent = new Agent
                            {
                                FirstName = teleportLureName.First(),
                                LastName = teleportLureName.Last(),
                                UUID = args.IM.FromAgentID,
                            },
                            Session = args.IM.IMSessionID
                        });
                    }
                    // Send teleport lure notification.
                    new Thread(o => SendNotification(Notifications.NOTIFICATION_TELEPORT_LURE, args))
                    {
                        IsBackground = true
                    }.Start();
                    // If we got a teleport request from a master, then accept it (for the moment).
                    if (Configuration.MASTERS.Select(
                        o =>
                            string.Format(CultureInfo.InvariantCulture, "{0} {1}", o.FirstName, o.LastName))
                        .
                        Any(p => p.Equals(args.IM.FromAgentName, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (Client.Self.Movement.SitOnGround || !Client.Self.SittingOn.Equals(0))
                        {
                            Client.Self.Stand();
                        }
                        Client.Self.SignaledAnimations.ForEach(
                            animation => Client.Self.AnimationStop(animation.Key, true));
                        lock (TeleportLock)
                        {
                            Client.Self.TeleportLureRespond(args.IM.FromAgentID, args.IM.IMSessionID, true);
                        }
                        return;
                    }
                    return;
                    // Group invitations received
                case InstantMessageDialog.GroupInvitation:
                    OpenMetaverse.Group inviteGroup = new OpenMetaverse.Group();
                    if (!RequestGroup(args.IM.FromAgentID, Configuration.SERVICES_TIMEOUT, ref inviteGroup)) return;
                    List<string> groupInviteName =
                        new List<string>(
                            GetAvatarNames(args.IM.FromAgentName));
                    UUID inviteGroupAgent = UUID.Zero;
                    if (
                        !AgentNameToUUID(groupInviteName.First(), groupInviteName.Last(), Configuration.SERVICES_TIMEOUT,
                            ref inviteGroupAgent)) return;
                    // Add the group invite - have to track them manually.
                    lock (GroupInviteLock)
                    {
                        GroupInvites.Add(new GroupInvite
                        {
                            Agent = new Agent
                            {
                                FirstName = groupInviteName.First(),
                                LastName = groupInviteName.Last(),
                                UUID = inviteGroupAgent
                            },
                            Group = inviteGroup.Name,
                            Session = args.IM.IMSessionID,
                            Fee = inviteGroup.MembershipFee
                        });
                    }
                    // Send group invitation notification.
                    new Thread(o => SendNotification(Notifications.NOTIFICATION_GROUP_INVITE, args))
                    {
                        IsBackground = true
                    }.Start();
                    // If a master sends it, then accept.
                    if (
                        !Configuration.MASTERS.Select(
                            o =>
                                string.Format(CultureInfo.InvariantCulture, "{0}.{1}", o.FirstName, o.LastName))
                            .
                            Any(p => p.Equals(args.IM.FromAgentName, StringComparison.OrdinalIgnoreCase)))
                        return;
                    Client.Self.GroupInviteRespond(inviteGroup.ID, args.IM.IMSessionID, true);
                    return;
                    // Group notice inventory accepted, declined or notice received.
                case InstantMessageDialog.GroupNoticeInventoryAccepted:
                case InstantMessageDialog.GroupNoticeInventoryDeclined:
                case InstantMessageDialog.GroupNotice:
                    new Thread(o => SendNotification(Notifications.NOTIFICATION_GROUP_NOTICE, args))
                    {
                        IsBackground = true
                    }.Start();
                    return;
                case InstantMessageDialog.SessionSend:
                case InstantMessageDialog.MessageFromAgent:
                    // Check if this is a group message.
                    // Note that this is a lousy way of doing it but libomv does not properly set the GroupIM field
                    // such that the only way to determine if we have a group message is to check that the UUID
                    // of the session is actually the UUID of a current group. Furthermore, what's worse is that 
                    // group mesages can appear both through SessionSend and from MessageFromAgent. Hence the problem.
                    HashSet<OpenMetaverse.Group> groups = new HashSet<OpenMetaverse.Group>();
                    if (!RequestCurrentGroups(Configuration.SERVICES_TIMEOUT, ref groups)) return;
                    bool messageFromGroup = groups.Any(o => o.ID.Equals(args.IM.IMSessionID));
                    OpenMetaverse.Group messageGroup = groups.FirstOrDefault(o => o.ID.Equals(args.IM.IMSessionID));
                    if (messageFromGroup)
                    {
                        // Send group notice notifications.
                        new Thread(o => SendNotification(Notifications.NOTIFICATION_GROUP_MESSAGE, args))
                        {
                            IsBackground = true
                        }.Start();
                        // Log group messages
                        Parallel.ForEach(
                            Configuration.GROUPS.Where(o => o.Name.Equals(messageGroup.Name, StringComparison.Ordinal)),
                            o =>
                            {
                                // Attempt to write to log file,
                                try
                                {
                                    lock (LogFileLock)
                                    {
                                        using (StreamWriter logWriter = File.AppendText(o.ChatLog))
                                        {
                                            logWriter.WriteLine("[{0}] {1} : {2}",
                                                DateTime.Now.ToString(CORRADE_CONSTANTS.DATE_TIME_STAMP,
                                                    DateTimeFormatInfo.InvariantInfo), args.IM.FromAgentName, message);
                                            logWriter.Flush();
                                            //logWriter.Close();
                                        }
                                    }
                                }
                                catch (Exception e)
                                {
                                    // or fail and append the fail message.
                                    Feedback(
                                        wasGetDescriptionFromEnumValue(
                                            ConsoleError.COULD_NOT_WRITE_TO_GROUP_CHAT_LOGFILE),
                                        e.Message);
                                }
                            });
                        return;
                    }
                    // Check if this is an instant message.
                    if (args.IM.ToAgentID.Equals(Client.Self.AgentID))
                    {
                        new Thread(o => SendNotification(Notifications.NOTIFICATION_INSTANT_MESSAGE, args))
                        {
                            IsBackground = true
                        }.Start();
                        return;
                    }
                    // Check if this is a region message.
                    if (args.IM.IMSessionID.Equals(UUID.Zero))
                    {
                        new Thread(o => SendNotification(Notifications.NOTIFICATION_REGION_MESSAGE, args))
                        {
                            IsBackground = true
                        }.Start();
                        return;
                    }
                    break;
            }

            // Everything else, must be a command.
            new Thread(
                () => HandleCorradeCommand(args.IM.Message, args.IM.FromAgentName, args.IM.FromAgentID.ToString()))
            {
                IsBackground = true
            }.Start();
        }

        private static void HandleRLVCommand(string message, UUID senderUUID)
        {
            if (string.IsNullOrEmpty(message)) return;

            // Split all commands.
            string[] unpack = message.Split(',');
            // Pop first command to process.
            string first = unpack.First();
            // Remove command.
            unpack = unpack.Where(o => !o.Equals(first)).ToArray();
            // Keep rest of message.
            message = string.Join(RLV_CONSTANTS.CSV_DELIMITER, unpack);

            Match match = RLVRegex.Match(first);
            if (!match.Success) goto CONTINUE;

            RLVRule RLVrule = new RLVRule
            {
                Behaviour = match.Groups["behaviour"].ToString().ToLowerInvariant(),
                Option = match.Groups["option"].ToString().ToLowerInvariant(),
                Param = match.Groups["param"].ToString().ToLowerInvariant(),
                ObjectUUID = senderUUID
            };

            switch (RLVrule.Param)
            {
                case RLV_CONSTANTS.Y:
                case RLV_CONSTANTS.ADD:
                    lock (RLVRuleLock)
                    {
                        if (!RLVRules.Contains(RLVrule))
                        {
                            RLVRules.Add(RLVrule);
                        }
                    }
                    goto CONTINUE;
                case RLV_CONSTANTS.N:
                case RLV_CONSTANTS.REM:
                    lock (RLVRuleLock)
                    {
                        if (RLVRules.Contains(RLVrule))
                        {
                            RLVRules.Add(RLVrule);
                        }
                    }
                    goto CONTINUE;
            }

            System.Action execute;

            switch (wasGetEnumValueFromDescription<RLVBehaviour>(RLVrule.Behaviour))
            {
                case RLVBehaviour.VERSION:
                case RLVBehaviour.VERSIONNEW:
                    execute = () =>
                    {
                        int channel;
                        if (!int.TryParse(RLVrule.Param, out channel) || channel < 1)
                        {
                            return;
                        }
                        Client.Self.Chat(
                            string.Format("{0} v{1} (Corrade Version: {2} Compiled: {3})", RLV_CONSTANTS.VIEWER,
                                RLV_CONSTANTS.SHORT_VERSION, CORRADE_VERSION, CORRADE_COMPILE_DATE), channel,
                            ChatType.Normal);
                    };
                    break;
                case RLVBehaviour.VERSIONNUM:
                    execute = () =>
                    {
                        int channel;
                        if (!int.TryParse(RLVrule.Param, out channel) || channel < 1)
                        {
                            return;
                        }
                        Client.Self.Chat(RLV_CONSTANTS.LONG_VERSION, channel, ChatType.Normal);
                    };
                    break;
                case RLVBehaviour.GETGROUP:
                    execute = () =>
                    {
                        int channel;
                        if (!int.TryParse(RLVrule.Param, out channel) || channel < 1)
                        {
                            return;
                        }
                        UUID groupUUID = Client.Self.ActiveGroup;
                        HashSet<OpenMetaverse.Group> groups = new HashSet<OpenMetaverse.Group>();
                        if (!RequestCurrentGroups(Configuration.SERVICES_TIMEOUT, ref groups))
                        {
                            return;
                        }
                        if (!groups.Any(o => o.ID.Equals(groupUUID)))
                        {
                            return;
                        }
                        Client.Self.Chat(groups.FirstOrDefault(o => o.ID.Equals(groupUUID)).Name, channel,
                            ChatType.Normal);
                    };
                    break;
                case RLVBehaviour.SETGROUP:
                    execute = () =>
                    {
                        if (!RLVrule.Param.Equals(RLV_CONSTANTS.FORCE))
                        {
                            return;
                        }
                        HashSet<OpenMetaverse.Group> groups = new HashSet<OpenMetaverse.Group>();
                        if (!RequestCurrentGroups(Configuration.SERVICES_TIMEOUT, ref groups))
                        {
                            return;
                        }
                        UUID groupUUID;
                        if (!UUID.TryParse(RLVrule.Option, out groupUUID))
                        {
                            return;
                        }
                        if (!groups.Any(o => o.ID.Equals(groupUUID)))
                        {
                            return;
                        }
                        Client.Groups.ActivateGroup(groups.FirstOrDefault(o => o.ID.Equals(groupUUID)).ID);
                    };
                    break;
                case RLVBehaviour.GETSITID:
                    execute = () =>
                    {
                        int channel;
                        if (!int.TryParse(RLVrule.Param, out channel) || channel < 1)
                        {
                            return;
                        }
                        Avatar me;
                        if (Client.Network.CurrentSim.ObjectsAvatars.TryGetValue(Client.Self.LocalID, out me))
                        {
                            if (me.ParentID != 0)
                            {
                                Primitive sit;
                                if (Client.Network.CurrentSim.ObjectsPrimitives.TryGetValue(me.ParentID, out sit))
                                {
                                    Client.Self.Chat(sit.ID.ToString(), channel, ChatType.Normal);
                                    return;
                                }
                            }
                        }
                        UUID zero = UUID.Zero;
                        Client.Self.Chat(zero.ToString(), channel, ChatType.Normal);
                    };
                    break;
                case RLVBehaviour.SIT:
                    execute = () =>
                    {
                        UUID sitTarget;
                        if (!RLVrule.Param.Equals(RLV_CONSTANTS.FORCE) || !UUID.TryParse(RLVrule.Option, out sitTarget) ||
                            sitTarget.Equals(UUID.Zero))
                        {
                            return;
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(sitTarget.ToString(),
                                LINDEN_CONSTANTS.LSL.SENSOR_RANGE,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            return;
                        }
                        ManualResetEvent SitEvent = new ManualResetEvent(false);
                        EventHandler<AvatarSitResponseEventArgs> AvatarSitEventHandler =
                            (sender, args) => SitEvent.Set();
                        EventHandler<AlertMessageEventArgs> AlertMessageEventHandler = (sender, args) => SitEvent.Set();
                        if (Client.Self.Movement.SitOnGround || !Client.Self.SittingOn.Equals(0))
                        {
                            Client.Self.Stand();
                        }
                        Client.Self.SignaledAnimations.ForEach(
                            animation => Client.Self.AnimationStop(animation.Key, true));
                        lock (ServicesLock)
                        {
                            Client.Self.AvatarSitResponse += AvatarSitEventHandler;
                            Client.Self.AlertMessage += AlertMessageEventHandler;
                            Client.Self.RequestSit(primitive.ID, Vector3.Zero);
                            SitEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false);
                            Client.Self.AvatarSitResponse -= AvatarSitEventHandler;
                            Client.Self.AlertMessage -= AlertMessageEventHandler;
                        }
                    };
                    break;
                case RLVBehaviour.UNSIT:
                    execute = () =>
                    {
                        if (!RLVrule.Param.Equals(RLV_CONSTANTS.FORCE))
                        {
                            return;
                        }
                        if (Client.Self.Movement.SitOnGround || !Client.Self.SittingOn.Equals(0))
                        {
                            Client.Self.Stand();
                        }
                        Client.Self.SignaledAnimations.ForEach(
                            animation => Client.Self.AnimationStop(animation.Key, true));
                    };
                    break;
                case RLVBehaviour.SETROT:
                    execute = () =>
                    {
                        double rotation = 0;
                        if (!RLVrule.Param.Equals(RLV_CONSTANTS.FORCE) ||
                            !double.TryParse(RLVrule.Option, NumberStyles.Float, CultureInfo.InvariantCulture,
                                out rotation))
                        {
                            Client.Self.Movement.UpdateFromHeading(Math.PI/2d - rotation, true);
                        }
                    };
                    break;
                case RLVBehaviour.TPTO:
                    execute = () =>
                    {
                        string[] coordinates = RLVrule.Option.Split('/');
                        if (!coordinates.Length.Equals(3))
                        {
                            return;
                        }
                        float globalX;
                        if (!float.TryParse(coordinates[0], out globalX))
                        {
                            return;
                        }
                        float globalY;
                        if (!float.TryParse(coordinates[1], out globalY))
                        {
                            return;
                        }
                        float altitude;
                        if (!float.TryParse(coordinates[2], out altitude))
                        {
                            return;
                        }
                        float localX, localY;
                        ulong handle = Helpers.GlobalPosToRegionHandle(globalX, globalY, out localX, out localY);
                        lock (TeleportLock)
                        {
                            Client.Self.RequestTeleport(handle, new Vector3(localX, localY, altitude));
                        }
                    };
                    break;
                case RLVBehaviour.GETOUTFIT:
                    execute = () =>
                    {
                        int channel;
                        if (!int.TryParse(RLVrule.Param, out channel) || channel < 1)
                        {
                            return;
                        }
                        HashSet<KeyValuePair<WearableType, string>> wearables =
                            new HashSet<KeyValuePair<WearableType, string>>(GetWearables(Client.Inventory.Store.RootNode));
                        StringBuilder response = new StringBuilder();
                        switch (!string.IsNullOrEmpty(RLVrule.Option))
                        {
                            case true:
                                WearableType wearableType =
                                    RLVWearables.FirstOrDefault(
                                        o => o.Name.Equals(RLVrule.Option, StringComparison.InvariantCultureIgnoreCase))
                                        .WearableType;
                                if (!wearables.Any(o => o.Key.Equals(wearableType)))
                                {
                                    response.Append(RLV_CONSTANTS.FALSE_MARKER);
                                    break;
                                }
                                response.Append(RLV_CONSTANTS.TRUE_MARKER);
                                break;
                            default:
                                string[] data = new string[RLVWearables.Count];
                                Parallel.ForEach(Enumerable.Range(0, RLVWearables.Count), o =>
                                {
                                    if (!wearables.Any(p => p.Key.Equals(RLVWearables[o].WearableType)))
                                    {
                                        data[o] = RLV_CONSTANTS.FALSE_MARKER;
                                        return;
                                    }
                                    data[o] = RLV_CONSTANTS.TRUE_MARKER;
                                });
                                response.Append(string.Join("", data.ToArray()));
                                break;
                        }
                        Client.Self.Chat(response.ToString(), channel, ChatType.Normal);
                    };
                    break;
                case RLVBehaviour.GETATTACH:
                    execute = () =>
                    {
                        int channel;
                        if (!int.TryParse(RLVrule.Param, out channel) || channel < 1)
                        {
                            return;
                        }
                        HashSet<Primitive> attachments = new HashSet<Primitive>(
                            GetAttachments(Configuration.SERVICES_TIMEOUT).Select(o => o.Key));
                        StringBuilder response = new StringBuilder();
                        if (attachments.Count.Equals(0))
                        {
                            Client.Self.Chat(response.ToString(), channel, ChatType.Normal);
                        }
                        HashSet<AttachmentPoint> attachmentPoints =
                            new HashSet<AttachmentPoint>(attachments.Select(o => o.PrimData.AttachmentPoint));
                        switch (!string.IsNullOrEmpty(RLVrule.Option))
                        {
                            case true:
                                AttachmentPoint attachmentPoint =
                                    RLVAttachments.FirstOrDefault(
                                        o => o.Name.Equals(RLVrule.Option, StringComparison.InvariantCultureIgnoreCase))
                                        .AttachmentPoint;
                                if (!attachmentPoints.Contains(attachmentPoint))
                                {
                                    response.Append(RLV_CONSTANTS.FALSE_MARKER);
                                    break;
                                }
                                response.Append(RLV_CONSTANTS.TRUE_MARKER);
                                break;
                            default:
                                string[] data = new string[RLVAttachments.Count];
                                Parallel.ForEach(Enumerable.Range(0, RLVAttachments.Count), o =>
                                {
                                    if (!attachmentPoints.Contains(RLVAttachments[o].AttachmentPoint))
                                    {
                                        data[o] = RLV_CONSTANTS.FALSE_MARKER;
                                        return;
                                    }
                                    data[o] = RLV_CONSTANTS.TRUE_MARKER;
                                });
                                response.Append(string.Join("", data.ToArray()));
                                break;
                        }
                        Client.Self.Chat(response.ToString(), channel, ChatType.Normal);
                    };
                    break;
                case RLVBehaviour.DETACHME:
                    execute = () =>
                    {
                        if (!RLVrule.Param.Equals(RLV_CONSTANTS.FORCE))
                        {
                            return;
                        }
                        KeyValuePair<Primitive, AttachmentPoint> attachment =
                            GetAttachments(Configuration.SERVICES_TIMEOUT)
                                .FirstOrDefault(o => o.Key.ID.Equals(senderUUID));
                        if (attachment.Equals(default(KeyValuePair<Primitive, AttachmentPoint>)))
                        {
                            return;
                        }
                        InventoryBase inventoryBase =
                            FindInventory<InventoryBase>(Client.Inventory.Store.RootNode,
                                attachment.Key.Properties.ItemID
                                )
                                .FirstOrDefault(
                                    p =>
                                        (p is InventoryItem) &&
                                        ((InventoryItem) p).AssetType.Equals(AssetType.Object));
                        if (inventoryBase is InventoryAttachment || inventoryBase is InventoryObject)
                        {
                            Detach(inventoryBase as InventoryItem, Configuration.SERVICES_TIMEOUT);
                        }
                        if (!Rebake(Configuration.SERVICES_TIMEOUT, Configuration.REBAKE_DELAY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.REBAKE_FAILED));
                        }
                    };
                    break;
                case RLVBehaviour.REMATTACH:
                case RLVBehaviour.DETACH:
                    execute = () =>
                    {
                        if (!RLVrule.Param.Equals(RLV_CONSTANTS.FORCE))
                        {
                            return;
                        }
                        InventoryNode RLVFolder =
                            FindInventory<InventoryNode>(Client.Inventory.Store.RootNode,
                                RLV_CONSTANTS.SHARED_FOLDER_NAME).FirstOrDefault(o => o.Data is InventoryFolder);
                        if (RLVFolder == null)
                        {
                            return;
                        }
                        InventoryBase inventoryBase;
                        switch (!string.IsNullOrEmpty(RLVrule.Option))
                        {
                            case true:
                                RLVAttachment RLVattachment =
                                    RLVAttachments.FirstOrDefault(
                                        o => o.Name.Equals(RLVrule.Option, StringComparison.InvariantCultureIgnoreCase));
                                switch (!RLVattachment.Equals(default(RLVAttachment)))
                                {
                                    case true: // detach by attachment point
                                        Parallel.ForEach(
                                            GetAttachments(Configuration.SERVICES_TIMEOUT)
                                                .Where(o => o.Value.Equals(RLVattachment.AttachmentPoint)), o =>
                                                {
                                                    inventoryBase =
                                                        FindInventory<InventoryBase>(Client.Inventory.Store.RootNode,
                                                            o.Key.Properties.Name
                                                            )
                                                            .FirstOrDefault(
                                                                p =>
                                                                    (p is InventoryItem) &&
                                                                    ((InventoryItem) p).AssetType.Equals(
                                                                        AssetType.Object));
                                                    if (inventoryBase is InventoryAttachment ||
                                                        inventoryBase is InventoryObject)
                                                    {
                                                        Detach(inventoryBase as InventoryItem,
                                                            Configuration.SERVICES_TIMEOUT);
                                                    }
                                                });
                                        break;
                                    default: // detach by folder(s) name
                                        Parallel.ForEach(
                                            RLVrule.Option.Split(RLV_CONSTANTS.PATH_SEPARATOR[0])
                                                .Select(
                                                    folder =>
                                                        FindInventory<InventoryBase>(RLVFolder,
                                                            folder
                                                            ).FirstOrDefault(o => (o is InventoryFolder))), o =>
                                                            {
                                                                if (o != null)
                                                                {
                                                                    List<InventoryBase> folderContents;
                                                                    lock (InventoryLock)
                                                                    {
                                                                        folderContents =
                                                                            Client.Inventory.Store.GetContents(
                                                                                o as InventoryFolder);
                                                                    }
                                                                    folderContents.FindAll(CanBeWorn)
                                                                        .ForEach(
                                                                            p =>
                                                                            {
                                                                                if (p is InventoryWearable)
                                                                                {
                                                                                    UnWear(p as InventoryItem,
                                                                                        Configuration
                                                                                            .SERVICES_TIMEOUT);
                                                                                    return;
                                                                                }
                                                                                if (p is InventoryAttachment ||
                                                                                    p is InventoryObject)
                                                                                {
                                                                                    // Multiple attachment points not working in libOpenMetaverse, so just replace.
                                                                                    Detach(p as InventoryItem,
                                                                                        Configuration
                                                                                            .SERVICES_TIMEOUT);
                                                                                }
                                                                            });
                                                                }
                                                            });
                                        break;
                                }
                                break;
                            default: //detach everything from RLV attachmentpoints
                                Parallel.ForEach(
                                    GetAttachments(Configuration.SERVICES_TIMEOUT)
                                        .Where(o => RLVAttachments.Any(p => p.AttachmentPoint.Equals(o.Value))), o =>
                                        {
                                            inventoryBase = FindInventory<InventoryBase>(
                                                Client.Inventory.Store.RootNode, o.Key.Properties.Name
                                                )
                                                .FirstOrDefault(
                                                    p =>
                                                        p is InventoryItem &&
                                                        ((InventoryItem) p).AssetType.Equals(AssetType.Object));
                                            if (inventoryBase is InventoryAttachment || inventoryBase is InventoryObject)
                                            {
                                                Detach(inventoryBase as InventoryItem, Configuration.SERVICES_TIMEOUT);
                                            }
                                        });
                                break;
                        }
                        if (!Rebake(Configuration.SERVICES_TIMEOUT, Configuration.REBAKE_DELAY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.REBAKE_FAILED));
                        }
                    };
                    break;
                case RLVBehaviour.ATTACH:
                case RLVBehaviour.ATTACHOVERORREPLACE:
                case RLVBehaviour.ATTACHOVER:
                    execute = () =>
                    {
                        if (!RLVrule.Param.Equals(RLV_CONSTANTS.FORCE) || string.IsNullOrEmpty(RLVrule.Option))
                        {
                            return;
                        }
                        InventoryNode RLVFolder =
                            FindInventory<InventoryNode>(Client.Inventory.Store.RootNode,
                                RLV_CONSTANTS.SHARED_FOLDER_NAME).FirstOrDefault(o => o.Data is InventoryFolder);
                        if (RLVFolder == null)
                        {
                            return;
                        }
                        Parallel.ForEach(
                            RLVrule.Option.Split(RLV_CONSTANTS.PATH_SEPARATOR[0])
                                .Select(
                                    folder =>
                                        FindInventory<InventoryBase>(RLVFolder,
                                            folder
                                            ).FirstOrDefault(o => (o is InventoryFolder))), o =>
                                            {
                                                if (o != null)
                                                {
                                                    List<InventoryBase> folderContents;
                                                    lock (InventoryLock)
                                                    {
                                                        folderContents =
                                                            Client.Inventory.Store.GetContents(o as InventoryFolder);
                                                    }
                                                    folderContents.
                                                        FindAll(CanBeWorn)
                                                        .ForEach(
                                                            p =>
                                                            {
                                                                if (p is InventoryWearable)
                                                                {
                                                                    Wear(p as InventoryItem, true,
                                                                        Configuration.SERVICES_TIMEOUT);
                                                                    return;
                                                                }
                                                                if (p is InventoryObject || p is InventoryAttachment)
                                                                {
                                                                    // Multiple attachment points not working in libOpenMetaverse, so just replace.
                                                                    Attach(p as InventoryItem,
                                                                        AttachmentPoint.Default,
                                                                        true,
                                                                        Configuration.SERVICES_TIMEOUT);
                                                                }
                                                            });
                                                }
                                            });

                        if (!Rebake(Configuration.SERVICES_TIMEOUT, Configuration.REBAKE_DELAY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.REBAKE_FAILED));
                        }
                    };
                    break;
                case RLVBehaviour.REMOUTFIT:
                    execute = () =>
                    {
                        if (!RLVrule.Param.Equals(RLV_CONSTANTS.FORCE))
                        {
                            return;
                        }
                        InventoryBase inventoryBase;
                        switch (!string.IsNullOrEmpty(RLVrule.Option))
                        {
                            case true: // A single wearable
                                FieldInfo wearTypeInfo = typeof (WearableType).GetFields(BindingFlags.Public |
                                                                                         BindingFlags.Static)
                                    .FirstOrDefault(
                                        p => p.Name.Equals(RLVrule.Option, StringComparison.InvariantCultureIgnoreCase));
                                if (wearTypeInfo == null)
                                {
                                    break;
                                }
                                inventoryBase =
                                    FindInventory<InventoryBase>(Client.Inventory.Store.RootNode,
                                        GetWearables(Client.Inventory.Store.RootNode)
                                            .FirstOrDefault(
                                                o => o.Key.Equals((WearableType) wearTypeInfo.GetValue(null)))
                                            .Value).FirstOrDefault();
                                if (inventoryBase == null)
                                {
                                    break;
                                }
                                UnWear(inventoryBase as InventoryItem, Configuration.SERVICES_TIMEOUT);
                                break;
                            default:
                                Parallel.ForEach(GetWearables(Client.Inventory.Store.RootNode)
                                    .Select(o => new[]
                                    {
                                        o.Value
                                    }).SelectMany(o => o), o =>
                                    {
                                        inventoryBase =
                                            FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, o
                                                )
                                                .FirstOrDefault(p => (p is InventoryWearable));
                                        if (inventoryBase == null)
                                        {
                                            return;
                                        }
                                        UnWear(inventoryBase as InventoryItem, Configuration.SERVICES_TIMEOUT);
                                    });
                                break;
                        }
                        if (!Rebake(Configuration.SERVICES_TIMEOUT, Configuration.REBAKE_DELAY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.REBAKE_FAILED));
                        }
                    };
                    break;
                case RLVBehaviour.GETPATHNEW:
                case RLVBehaviour.GETPATH:
                    execute = () =>
                    {
                        int channel;
                        if (!int.TryParse(RLVrule.Param, out channel) || channel < 1)
                        {
                            return;
                        }
                        InventoryNode RLVFolder =
                            FindInventory<InventoryNode>(Client.Inventory.Store.RootNode,
                                RLV_CONSTANTS.SHARED_FOLDER_NAME).FirstOrDefault(o => o.Data is InventoryFolder);
                        if (RLVFolder == null)
                        {
                            Client.Self.Chat(string.Empty, channel, ChatType.Normal);
                            return;
                        }
                        // General variables
                        InventoryBase inventoryBase = null;
                        KeyValuePair<Primitive, AttachmentPoint> attachment;
                        switch (!string.IsNullOrEmpty(RLVrule.Option))
                        {
                            case true:
                                // Try attachments
                                RLVAttachment RLVattachment =
                                    RLVAttachments.FirstOrDefault(
                                        o => o.Name.Equals(RLVrule.Option, StringComparison.InvariantCultureIgnoreCase));
                                if (!RLVattachment.Equals(default(RLVAttachment)))
                                {
                                    attachment =
                                        GetAttachments(Configuration.SERVICES_TIMEOUT)
                                            .FirstOrDefault(o => o.Value.Equals(RLVattachment.AttachmentPoint));
                                    if (attachment.Equals(default(KeyValuePair<Primitive, AttachmentPoint>)))
                                    {
                                        return;
                                    }
                                    inventoryBase = FindInventory<InventoryBase>(
                                        RLVFolder, RLVrule.Option
                                        )
                                        .FirstOrDefault(
                                            p =>
                                                (p is InventoryItem) &&
                                                ((InventoryItem) p).AssetType.Equals(AssetType.Object));
                                    break;
                                }
                                RLVWearable RLVwearable =
                                    RLVWearables.FirstOrDefault(
                                        o => o.Name.Equals(RLVrule.Option, StringComparison.InvariantCultureIgnoreCase));
                                if (!RLVwearable.Equals(default(RLVWearable)))
                                {
                                    FieldInfo wearTypeInfo = typeof (WearableType).GetFields(BindingFlags.Public |
                                                                                             BindingFlags.Static)
                                        .FirstOrDefault(
                                            p =>
                                                p.Name.Equals(RLVrule.Option,
                                                    StringComparison.InvariantCultureIgnoreCase));
                                    if (wearTypeInfo == null)
                                    {
                                        return;
                                    }
                                    inventoryBase =
                                        FindInventory<InventoryBase>(RLVFolder,
                                            GetWearables(RLVFolder)
                                                .FirstOrDefault(
                                                    o => o.Key.Equals((WearableType) wearTypeInfo.GetValue(null)))
                                                .Value)
                                            .FirstOrDefault(o => (o is InventoryWearable));
                                }
                                break;
                            default:
                                attachment =
                                    GetAttachments(Configuration.SERVICES_TIMEOUT)
                                        .FirstOrDefault(o => o.Key.ID.Equals(senderUUID));
                                if (attachment.Equals(default(KeyValuePair<Primitive, AttachmentPoint>)))
                                {
                                    break;
                                }
                                inventoryBase = FindInventory<InventoryBase>(
                                    Client.Inventory.Store.RootNode, attachment.Key.Properties.ItemID
                                    )
                                    .FirstOrDefault(
                                        p =>
                                            (p is InventoryItem) &&
                                            ((InventoryItem) p).AssetType.Equals(AssetType.Object));
                                break;
                        }
                        if (inventoryBase == null)
                        {
                            Client.Self.Chat(string.Empty, channel, ChatType.Normal);
                            return;
                        }
                        KeyValuePair<InventoryBase, LinkedList<string>> path =
                            FindInventoryPath<InventoryBase>(RLVFolder, inventoryBase.Name,
                                new LinkedList<string>()).FirstOrDefault();
                        if (path.Equals(default(KeyValuePair<InventoryBase, LinkedList<string>>)))
                        {
                            Client.Self.Chat(string.Empty, channel, ChatType.Normal);
                            return;
                        }
                        Client.Self.Chat(string.Join(CORRADE_CONSTANTS.PATH_SEPARATOR, path.Value.ToArray()), channel,
                            ChatType.Normal);
                    };
                    break;
                case RLVBehaviour.FINDFOLDER:
                    execute = () =>
                    {
                        int channel;
                        if (!int.TryParse(RLVrule.Param, out channel) || channel < 1)
                        {
                            return;
                        }
                        if (string.IsNullOrEmpty(RLVrule.Option))
                        {
                            Client.Self.Chat(string.Empty, channel, ChatType.Normal);
                            return;
                        }
                        InventoryNode RLVFolder =
                            FindInventory<InventoryNode>(Client.Inventory.Store.RootNode,
                                RLV_CONSTANTS.SHARED_FOLDER_NAME).FirstOrDefault(o => o.Data is InventoryFolder);
                        if (RLVFolder == null)
                        {
                            Client.Self.Chat(string.Empty, channel, ChatType.Normal);
                            return;
                        }
                        List<string> folders = new List<string>();
                        HashSet<string> parts =
                            new HashSet<string>(RLVrule.Option.Split(RLV_CONSTANTS.AND_OPERATOR.ToCharArray()));
                        object LockObject = new object();
                        Parallel.ForEach(FindInventoryPath<InventoryBase>(RLVFolder,
                            new Regex(".+?", RegexOptions.Compiled),
                            new LinkedList<string>())
                            .Where(
                                o =>
                                    o.Key is InventoryFolder &&
                                    !o.Key.Name.Substring(1).Equals(RLV_CONSTANTS.DOT_MARKER) &&
                                    !o.Key.Name.Substring(1).Equals(RLV_CONSTANTS.TILDE_MARKER)), o =>
                                    {
                                        int count = 0;
                                        Parallel.ForEach(parts, p => Parallel.ForEach(o.Value, q =>
                                        {
                                            if (q.Contains(p))
                                            {
                                                Interlocked.Increment(ref count);
                                            }
                                        }));
                                        if (!count.Equals(parts.Count)) return;
                                        lock (LockObject)
                                        {
                                            folders.Add(o.Key.Name);
                                        }
                                    });
                        if (!folders.Count.Equals(0))
                        {
                            Client.Self.Chat(string.Join(CORRADE_CONSTANTS.PATH_SEPARATOR, folders.ToArray()),
                                channel,
                                ChatType.Normal);
                        }
                    };
                    break;
                case RLVBehaviour.GETINV:
                    execute = () =>
                    {
                        int channel;
                        if (!int.TryParse(RLVrule.Param, out channel) || channel < 1)
                        {
                            return;
                        }
                        if (string.IsNullOrEmpty(RLVrule.Option))
                        {
                            Client.Self.Chat(string.Empty, channel, ChatType.Normal);
                            return;
                        }
                        InventoryNode RLVFolder =
                            FindInventory<InventoryNode>(Client.Inventory.Store.RootNode,
                                RLV_CONSTANTS.SHARED_FOLDER_NAME).FirstOrDefault(o => o.Data is InventoryFolder);
                        if (RLVFolder == null)
                        {
                            Client.Self.Chat(string.Empty, channel, ChatType.Normal);
                            return;
                        }
                        InventoryNode optionFolderNode =
                            FindInventory<InventoryNode>(RLVFolder, RLVrule.Option).FirstOrDefault();
                        if (optionFolderNode == null)
                        {
                            Client.Self.Chat(string.Empty, channel, ChatType.Normal);
                            return;
                        }
                        HashSet<string> csv = new HashSet<string>();
                        object LockObject = new object();
                        Parallel.ForEach(
                            FindInventory<InventoryBase>(optionFolderNode, new Regex(".+?", RegexOptions.Compiled)),
                            o =>
                            {
                                if (o.Name.StartsWith(RLV_CONSTANTS.DOT_MARKER)) return;
                                lock (LockObject)
                                {
                                    csv.Add(o.Name);
                                }
                            });
                        Client.Self.Chat(string.Join(RLV_CONSTANTS.CSV_DELIMITER, csv.ToArray()), channel,
                            ChatType.Normal);
                    };
                    break;
                case RLVBehaviour.GETINVWORN:
                    execute = () =>
                    {
                        int channel;
                        if (!int.TryParse(RLVrule.Param, out channel) || channel < 1)
                        {
                            return;
                        }
                        InventoryNode RLVFolder =
                            FindInventory<InventoryNode>(Client.Inventory.Store.RootNode,
                                RLV_CONSTANTS.SHARED_FOLDER_NAME).FirstOrDefault(o => o.Data is InventoryFolder);
                        if (RLVFolder == null)
                        {
                            Client.Self.Chat(string.Empty, channel, ChatType.Normal);
                            return;
                        }
                        KeyValuePair<InventoryNode, LinkedList<string>> folderPath = FindInventoryPath<InventoryNode>(
                            RLVFolder,
                            new Regex(".+?", RegexOptions.Compiled),
                            new LinkedList<string>())
                            .Where(o => o.Key.Data is InventoryFolder)
                            .FirstOrDefault(
                                o =>
                                    string.Join(RLV_CONSTANTS.PATH_SEPARATOR, o.Value.Skip(1).ToArray())
                                        .Equals(RLVrule.Option, StringComparison.InvariantCultureIgnoreCase));
                        if (folderPath.Equals(default(KeyValuePair<InventoryNode, LinkedList<string>>)))
                        {
                            Client.Self.Chat(string.Empty, channel, ChatType.Normal);
                            return;
                        }
                        Func<InventoryNode, string> GetWornIndicator = node =>
                        {
                            Dictionary<WearableType, AppearanceManager.WearableData> currentWearables;
                            lock (InventoryLock)
                            {
                                currentWearables =
                                    Client.Appearance.GetWearables();
                            }
                            List<Primitive> currentAttachments;
                            lock (ServicesLock)
                            {
                                currentAttachments =
                                    Client.Network.CurrentSim.ObjectsPrimitives.FindAll(
                                        o => o.ParentID.Equals(Client.Self.LocalID));
                            }

                            int myItemsCount = 0;
                            int myItemsWornCount = 0;

                            lock (InventoryLock)
                            {
                                Parallel.ForEach(
                                    node.Nodes.Values.Where(
                                        n =>
                                            n.Data is InventoryItem && CanBeWorn(n.Data) &&
                                            !n.Data.Name.StartsWith(RLV_CONSTANTS.DOT_MARKER)), n =>
                                            {
                                                Interlocked.Increment(ref myItemsCount);
                                                if (n.Data is InventoryWearable &&
                                                    currentWearables.Values.Any(
                                                        o => o.ItemID.Equals(n.Data.UUID)) ||
                                                    currentAttachments.Any(
                                                        o =>
                                                            GetAttachments(Configuration.SERVICES_TIMEOUT)
                                                                .Any(
                                                                    p =>
                                                                        p.Key.Properties.ItemID.Equals(
                                                                            n.Data.UUID))))
                                                {
                                                    Interlocked.Increment(ref myItemsWornCount);
                                                }
                                            });
                            }

                            int allItemsCount = 0;
                            int allItemsWornCount = 0;

                            lock (InventoryLock)
                            {
                                Parallel.ForEach(
                                    node.Nodes.Values.Where(
                                        n =>
                                            n.Data is InventoryFolder &&
                                            !n.Data.Name.StartsWith(RLV_CONSTANTS.DOT_MARKER)),
                                    n => Parallel.ForEach(FindInventory<InventoryBase>(n,
                                        new Regex(".+?", RegexOptions.Compiled))
                                        .Where(o => !o.Name.StartsWith(RLV_CONSTANTS.DOT_MARKER))
                                        .Where(
                                            o =>
                                                o is InventoryItem && CanBeWorn(o) &&
                                                !o.Name.StartsWith(RLV_CONSTANTS.DOT_MARKER)), p =>
                                                {
                                                    Interlocked.Increment(ref allItemsCount);
                                                    if (p is InventoryWearable &&
                                                        currentWearables.Values.Any(o => o.ItemID.Equals(p.UUID)) ||
                                                        currentAttachments.Any(
                                                            o =>
                                                                GetAttachments(Configuration.SERVICES_TIMEOUT)
                                                                    .Any(q => q.Key.Properties.ItemID.Equals(p.UUID))))
                                                    {
                                                        Interlocked.Increment(ref allItemsWornCount);
                                                    }
                                                }));
                            }


                            Func<int, int, string> WornIndicator =
                                (all, one) => all > 0 ? (all.Equals(one) ? "3" : (one > 0 ? "2" : "1")) : "0";

                            return WornIndicator(myItemsCount, myItemsWornCount) +
                                   WornIndicator(allItemsCount, allItemsWornCount);
                        };
                        List<string> response = new List<string>
                        {
                            string.Format("{0}{1}", RLV_CONSTANTS.PROPORTION_SEPARATOR,
                                GetWornIndicator(folderPath.Key))
                        };
                        foreach (InventoryNode node in folderPath.Key.Nodes.Values)
                        {
                            response.AddRange(
                                FindInventory<InventoryNode>(node,
                                    new Regex(".+?", RegexOptions.Compiled)).Where(o => o.Data is InventoryFolder)
                                    .Select(
                                        o =>
                                            string.Format("{0}{1}{2}", o.Data.Name,
                                                RLV_CONSTANTS.PROPORTION_SEPARATOR, GetWornIndicator(o))));
                        }
                        Client.Self.Chat(string.Join(RLV_CONSTANTS.CSV_DELIMITER, response.ToArray()),
                            channel,
                            ChatType.Normal);
                    };
                    break;
                case RLVBehaviour.GETSTATUSALL:
                case RLVBehaviour.GETSTATUS:
                    execute = () =>
                    {
                        int channel;
                        if (!int.TryParse(RLVrule.Param, out channel) || channel < 1)
                        {
                            return;
                        }
                        string separator = RLV_CONSTANTS.PATH_SEPARATOR;
                        string filter = string.Empty;
                        if (!string.IsNullOrEmpty(RLVrule.Option))
                        {
                            string[] parts = RLVrule.Option.Split(RLV_CONSTANTS.STATUS_SEPARATOR[0]);
                            if (parts.Length > 1 && parts[1].Length > 0)
                            {
                                separator = parts[1].Substring(0, 1);
                            }
                            if (parts.Length > 0 && parts[0].Length > 0)
                            {
                                filter = parts[0].ToLowerInvariant();
                            }
                        }
                        StringBuilder response = new StringBuilder();
                        lock (RLVRuleLock)
                        {
                            object LockObject = new object();
                            Parallel.ForEach(RLVRules.Where(o =>
                                o.ObjectUUID.Equals(senderUUID) && o.Behaviour.Contains(filter)
                                ), o =>
                                {
                                    lock (LockObject)
                                    {
                                        response.AppendFormat("{0}{1}", separator, o.Behaviour);
                                    }
                                    if (!string.IsNullOrEmpty(o.Option))
                                    {
                                        lock (LockObject)
                                        {
                                            response.AppendFormat("{0}{1}", RLV_CONSTANTS.PATH_SEPARATOR, o.Option);
                                        }
                                    }
                                });
                        }
                        Client.Self.Chat(response.ToString(),
                            channel,
                            ChatType.Normal);
                    };
                    break;
                case RLVBehaviour.CLEAR:
                    execute = () =>
                    {
                        switch (!string.IsNullOrEmpty(RLVrule.Option))
                        {
                            case true:
                                lock (RLVRuleLock)
                                {
                                    RLVRules.RemoveWhere(o => o.Behaviour.Contains(RLVrule.Behaviour));
                                }
                                break;
                            case false:
                                lock (RLVRuleLock)
                                {
                                    RLVRules.RemoveWhere(o => o.ObjectUUID.Equals(senderUUID));
                                }
                                break;
                        }
                    };
                    break;
                default:
                    execute =
                        () =>
                        {
                            throw new Exception(string.Join(CORRADE_CONSTANTS.ERROR_SEPARATOR,
                                new[]
                                {
                                    wasGetDescriptionFromEnumValue(ConsoleError.BEHAVIOUR_NOT_IMPLEMENTED),
                                    RLVrule.Behaviour
                                }));
                        };
                    break;
            }

            try
            {
                execute.Invoke();
            }
            catch (Exception e)
            {
                Feedback(wasGetDescriptionFromEnumValue(ConsoleError.FAILED_TO_MANIFEST_RLV_BEHAVIOUR), e.Message);
            }

            CONTINUE:
            HandleRLVCommand(message, senderUUID);
        }

        private static Dictionary<string, string> HandleCorradeCommand(string message, string sender, string identifier)
        {
            // Now we can start processing commands.
            // Get group and password.
            string group =
                wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.GROUP), message));
            // Bail if no group set.
            if (string.IsNullOrEmpty(group)) return null;
            // Get password.
            string password =
                wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.PASSWORD), message));
            // Bail if no password set.
            if (string.IsNullOrEmpty(password)) return null;
            // Authenticate the request against the group password.
            if (!Authenticate(group, password))
            {
                Feedback(group, wasGetDescriptionFromEnumValue(ConsoleError.ACCESS_DENIED));
                return null;
            }
            // Censor password.
            message = wasKeyValueSet(wasGetDescriptionFromEnumValue(ScriptKeys.PASSWORD),
                CORRADE_CONSTANTS.PASSWORD_CENSOR, message);
            /*
             * OpenSim sends the primitive UUID through args.IM.FromAgentID while Second Life properly sends 
             * the agent UUID - which just shows how crap OpenSim really is. This tries to resolve 
             * args.IM.FromAgentID to a name, which is what Second Life does, otherwise it just sets the name 
             * to the name of the primitive sending the message.
             */
            if (Client.Network.CurrentSim.SimVersion.Contains(LINDEN_CONSTANTS.GRID.SECOND_LIFE))
            {
                UUID fromAgentID;
                if (UUID.TryParse(identifier, out fromAgentID) &&
                    !AgentUUIDToName(fromAgentID, Configuration.SERVICES_TIMEOUT, ref sender))
                {
                    Feedback(wasGetDescriptionFromEnumValue(ConsoleError.AGENT_NOT_FOUND), fromAgentID.ToString());
                    return null;
                }
            }
            // Log the command.
            Feedback(string.Format(CultureInfo.InvariantCulture, "{0} ({1}) : {2}", sender,
                identifier,
                message));

            // Perform the command.
            Dictionary<string, string> result = ProcessCommand(message);
            // send callback if registered
            string url =
                wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.CALLBACK), message));
            if (!string.IsNullOrEmpty(url) && CallbackQueue.Count < Configuration.CALLBACK_QUEUE_LENGTH)
            {
                lock (CallbackQueueLock)
                {
                    CallbackQueue.Enqueue(new CallbackQueueElement
                    {
                        URL = url,
                        message = wasKeyValueEscape(result)
                    });
                }
            }
            return result;
        }

        /// <summary>
        ///     This function is responsible for processing commands.
        /// </summary>
        /// <param name="message">the message</param>
        /// <returns>a dictionary of key-value pairs representing the results of the command</returns>
        private static Dictionary<string, string> ProcessCommand(string message)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            string command =
                wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.COMMAND), message));
            if (!string.IsNullOrEmpty(command))
            {
                result.Add(wasGetDescriptionFromEnumValue(ScriptKeys.COMMAND), command);
            }
            string group =
                wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.GROUP), message));
            if (!string.IsNullOrEmpty(group))
            {
                result.Add(wasGetDescriptionFromEnumValue(ScriptKeys.GROUP), group);
            }

            System.Action execute;

            switch (wasGetEnumValueFromDescription<ScriptKeys>(command))
            {
                case ScriptKeys.JOIN:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ALREADY_IN_GROUP));
                        }
                        OpenMetaverse.Group commandGroup = new OpenMetaverse.Group();
                        if (!RequestGroup(groupUUID, Configuration.SERVICES_TIMEOUT, ref commandGroup))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!commandGroup.OpenEnrollment)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_OPEN));
                        }
                        ManualResetEvent GroupJoinedReplyEvent = new ManualResetEvent(false);
                        EventHandler<GroupOperationEventArgs> GroupOperationEventHandler =
                            (sender, args) => GroupJoinedReplyEvent.Set();
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupJoinedReply += GroupOperationEventHandler;
                            Client.Groups.RequestJoinGroup(groupUUID);
                            if (!GroupJoinedReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupJoinedReply -= GroupOperationEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_JOINING_GROUP));
                            }
                            Client.Groups.GroupJoinedReply -= GroupOperationEventHandler;
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_JOIN_GROUP));
                        }
                    };
                    break;
                case ScriptKeys.CREATEGROUP:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        if (
                            !HasCorradePermission(group, (int) Permissions.PERMISSION_ECONOMY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        if (!UpdateBalance(Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_OBTAIN_MONEY_BALANCE));
                        }
                        if (Client.Self.Balance < Configuration.GROUP_CREATE_FEE)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INSUFFICIENT_FUNDS));
                        }
                        OpenMetaverse.Group commandGroup = new OpenMetaverse.Group
                        {
                            Name = group
                        };
                        wasCSVToStructure(
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message)),
                            ref commandGroup);
                        bool succeeded = false;
                        ManualResetEvent GroupCreatedReplyEvent = new ManualResetEvent(false);
                        EventHandler<GroupCreatedReplyEventArgs> GroupCreatedEventHandler = (sender, args) =>
                        {
                            succeeded = args.Success;
                            GroupCreatedReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupCreatedReply += GroupCreatedEventHandler;
                            Client.Groups.RequestCreateGroup(commandGroup);
                            if (!GroupCreatedReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupCreatedReply -= GroupCreatedEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_CREATING_GROUP));
                            }
                            Client.Groups.GroupCreatedReply -= GroupCreatedEventHandler;
                        }
                        if (!succeeded)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_CREATE_GROUP));
                        }
                    };
                    break;
                case ScriptKeys.INVITE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.Invite,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        if (AgentInGroup(agentUUID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ALREADY_IN_GROUP));
                        }
                        // role is optional
                        string role =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ROLE),
                                message));
                        UUID roleUUID = UUID.Zero;
                        if (!string.IsNullOrEmpty(role) && !UUID.TryParse(role, out roleUUID) &&
                            !RoleNameToRoleUUID(role, groupUUID,
                                Configuration.SERVICES_TIMEOUT, ref roleUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ROLE_NOT_FOUND));
                        }
                        // If the role is not everybody, then check for powers to assign to the specified role.
                        if (roleUUID.Equals(UUID.Zero))
                        {
                            if (
                                !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.AssignMember,
                                    Configuration.SERVICES_TIMEOUT))
                            {
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                            }
                        }
                        Client.Groups.Invite(groupUUID, new List<UUID> {roleUUID}, agentUUID);
                    };
                    break;
                case ScriptKeys.REPLYTOGROUPINVITE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        uint action =
                            (uint) wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION), message))
                                    .ToLowerInvariant());
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ALREADY_IN_GROUP));
                        }
                        UUID sessionUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.SESSION),
                                        message)),
                                out sessionUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_SESSION_SPECIFIED));
                        }
                        lock (GroupInviteLock)
                        {
                            if (!GroupInvites.Any(o => o.Session.Equals(sessionUUID)))
                            {
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_GROUP_INVITE_SESSION));
                            }
                        }
                        int amount;
                        lock (GroupInviteLock)
                        {
                            amount = GroupInvites.FirstOrDefault(o => o.Session.Equals(sessionUUID)).Fee;
                        }
                        if (!amount.Equals(0) && action.Equals((uint) Action.ACCEPT))
                        {
                            if (!HasCorradePermission(group, (int) Permissions.PERMISSION_ECONOMY))
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                            }
                            if (!UpdateBalance(Configuration.SERVICES_TIMEOUT))
                            {
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_OBTAIN_MONEY_BALANCE));
                            }
                            if (Client.Self.Balance < amount)
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INSUFFICIENT_FUNDS));
                            }
                        }
                        Client.Self.GroupInviteRespond(groupUUID, sessionUUID,
                            action.Equals((uint) Action.ACCEPT));
                    };
                    break;
                case ScriptKeys.GETGROUPINVITES:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        List<string> csv = new List<string>();
                        object LockObject = new object();
                        lock (GroupInviteLock)
                        {
                            Parallel.ForEach(GroupInvites, o =>
                            {
                                lock (LockObject)
                                {
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o.Agent, o.Agent.FirstName), o.Agent.FirstName});
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o.Agent, o.Agent.LastName), o.Agent.LastName});
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o.Agent, o.Agent.UUID), o.Agent.UUID.ToString()});
                                    csv.AddRange(new[] {wasGetStructureMemberDescription(o, o.Group), o.Group});
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o, o.Session), o.Session.ToString()});
                                    csv.AddRange(new[]
                                    {
                                        wasGetStructureMemberDescription(o, o.Fee),
                                        o.Fee.ToString(CultureInfo.InvariantCulture)
                                    });
                                }
                            });
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.EJECT:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.Eject,
                                Configuration.SERVICES_TIMEOUT) ||
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.RemoveMember,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        if (!AgentInGroup(agentUUID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        OpenMetaverse.Group commandGroup = new OpenMetaverse.Group();
                        if (!RequestGroup(groupUUID, Configuration.SERVICES_TIMEOUT, ref commandGroup))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        ManualResetEvent GroupRoleMembersReplyEvent = new ManualResetEvent(false);
                        EventHandler<GroupRolesMembersReplyEventArgs> GroupRoleMembersEventHandler = (sender, args) =>
                        {
                            if (args.RolesMembers.Any(
                                o => o.Key.Equals(commandGroup.OwnerRole) && o.Value.Equals(agentUUID)))
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.CANNOT_EJECT_OWNERS));
                            }
                            Parallel.ForEach(
                                args.RolesMembers.Where(
                                    o => o.Value.Equals(agentUUID)),
                                o => Client.Groups.RemoveFromRole(groupUUID, o.Key, agentUUID));
                            GroupRoleMembersReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupRoleMembersReply += GroupRoleMembersEventHandler;
                            Client.Groups.RequestGroupRolesMembers(groupUUID);
                            if (!GroupRoleMembersReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupRoleMembersReply -= GroupRoleMembersEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_GROUP_ROLE_MEMBERS));
                            }
                            Client.Groups.GroupRoleMembersReply -= GroupRoleMembersEventHandler;
                        }
                        ManualResetEvent GroupEjectEvent = new ManualResetEvent(false);
                        bool succeeded = false;
                        EventHandler<GroupOperationEventArgs> GroupOperationEventHandler = (sender, args) =>
                        {
                            succeeded = args.Success;
                            GroupEjectEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupMemberEjected += GroupOperationEventHandler;
                            Client.Groups.EjectUser(groupUUID, agentUUID);
                            if (!GroupEjectEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupMemberEjected -= GroupOperationEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_EJECTING_AGENT));
                            }
                            Client.Groups.GroupMemberEjected -= GroupOperationEventHandler;
                        }
                        if (!succeeded)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_EJECT_AGENT));
                        }
                    };
                    break;
                case ScriptKeys.GETGROUPACCOUNTSUMMARYDATA:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        int days;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.DAYS), message)),
                                out days))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_DAYS));
                        }
                        int interval;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.INTERVAL), message)),
                                out interval))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_INTERVAL));
                        }
                        ManualResetEvent RequestGroupAccountSummaryEvent = new ManualResetEvent(false);
                        GroupAccountSummary summary = new GroupAccountSummary();
                        EventHandler<GroupAccountSummaryReplyEventArgs> RequestGroupAccountSummaryEventHandler =
                            (sender, args) =>
                            {
                                summary = args.Summary;
                                RequestGroupAccountSummaryEvent.Set();
                            };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupAccountSummaryReply += RequestGroupAccountSummaryEventHandler;
                            Client.Groups.RequestGroupAccountSummary(groupUUID, days, interval);
                            if (!RequestGroupAccountSummaryEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupAccountSummaryReply -= RequestGroupAccountSummaryEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_GROUP_ACCOUNT_SUMMARY));
                            }
                            Client.Groups.GroupAccountSummaryReply -= RequestGroupAccountSummaryEventHandler;
                        }
                        List<string> data = new List<string>(GetStructuredData(summary,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message)))
                            );
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.UPDATEGROUPDATA:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        if (
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.ChangeIdentity,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        OpenMetaverse.Group commandGroup = new OpenMetaverse.Group();
                        if (!RequestGroup(groupUUID, Configuration.SERVICES_TIMEOUT, ref commandGroup))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        wasCSVToStructure(
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message)),
                            ref commandGroup);
                        Client.Groups.UpdateGroup(groupUUID, commandGroup);
                    };
                    break;
                case ScriptKeys.LEAVE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        ManualResetEvent GroupLeaveReplyEvent = new ManualResetEvent(false);
                        bool succeeded = false;
                        EventHandler<GroupOperationEventArgs> GroupOperationEventHandler = (sender, args) =>
                        {
                            succeeded = args.Success;
                            GroupLeaveReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupLeaveReply += GroupOperationEventHandler;
                            Client.Groups.LeaveGroup(groupUUID);
                            if (!GroupLeaveReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupLeaveReply -= GroupOperationEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_LEAVING_GROUP));
                            }
                            Client.Groups.GroupLeaveReply -= GroupOperationEventHandler;
                        }
                        if (!succeeded)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_LEAVE_GROUP));
                        }
                    };
                    break;
                case ScriptKeys.CREATEROLE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        if (
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.CreateRole,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        ManualResetEvent GroupRoleDataReplyEvent = new ManualResetEvent(false);
                        int roleCount = 0;
                        EventHandler<GroupRolesDataReplyEventArgs> GroupRolesDataEventHandler = (sender, args) =>
                        {
                            roleCount = args.Roles.Count;
                            GroupRoleDataReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupRoleDataReply += GroupRolesDataEventHandler;
                            Client.Groups.RequestGroupRoles(groupUUID);
                            if (!GroupRoleDataReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupRoleDataReply -= GroupRolesDataEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_GROUP_ROLES));
                            }
                            Client.Groups.GroupRoleDataReply -= GroupRolesDataEventHandler;
                        }
                        if (roleCount >= LINDEN_CONSTANTS.GROUPS.MAXIMUM_NUMBER_OF_ROLES)
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.MAXIMUM_NUMBER_OF_ROLES_EXCEEDED));
                        }
                        string role =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ROLE),
                                message));
                        if (string.IsNullOrEmpty(role))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_ROLE_NAME_SPECIFIED));
                        }
                        ulong powers = 0;
                        Parallel.ForEach(
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POWERS),
                                message))
                                .Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER}, StringSplitOptions.RemoveEmptyEntries),
                            o =>
                                Parallel.ForEach(
                                    typeof (GroupPowers).GetFields(BindingFlags.Public | BindingFlags.Static)
                                        .Where(p => p.Name.Equals(o, StringComparison.Ordinal)),
                                    q => { powers |= ((ulong) q.GetValue(null)); }));
                        if (!HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.ChangeActions,
                            Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        Client.Groups.CreateRole(groupUUID, new GroupRole
                        {
                            Name = role,
                            Description =
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DESCRIPTION),
                                        message)),
                            GroupID = groupUUID,
                            ID = UUID.Random(),
                            Powers = (GroupPowers) powers,
                            Title =
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.TITLE), message))
                        });
                        UUID roleUUID = UUID.Zero;
                        if (
                            !RoleNameToRoleUUID(role, groupUUID,
                                Configuration.SERVICES_TIMEOUT, ref roleUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_CREATE_ROLE));
                        }
                    };
                    break;
                case ScriptKeys.GETROLES:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        ManualResetEvent GroupRoleDataReplyEvent = new ManualResetEvent(false);
                        List<string> csv = new List<string>();
                        EventHandler<GroupRolesDataReplyEventArgs> GroupRolesDataEventHandler = (sender, args) =>
                        {
                            csv.AddRange(args.Roles.Select(o => new[]
                            {
                                o.Value.Name,
                                o.Value.ID.ToString(),
                                o.Value.Title,
                                o.Value.Description
                            }).SelectMany(o => o));
                            GroupRoleDataReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupRoleDataReply += GroupRolesDataEventHandler;
                            Client.Groups.RequestGroupRoles(groupUUID);
                            if (!GroupRoleDataReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupRoleDataReply -= GroupRolesDataEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_GROUP_ROLES));
                            }
                            Client.Groups.GroupRoleDataReply -= GroupRolesDataEventHandler;
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETMEMBERS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(o => o.Name.Equals(group, StringComparison.Ordinal))
                                .UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (
                            !AgentInGroup(Client.Self.AgentID, groupUUID,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        ManualResetEvent agentInGroupEvent = new ManualResetEvent(false);
                        List<string> csv = new List<string>();
                        EventHandler<GroupMembersReplyEventArgs> HandleGroupMembersReplyDelegate = (sender, args) =>
                        {
                            foreach (KeyValuePair<UUID, GroupMember> pair in args.Members)
                            {
                                string agentName = string.Empty;
                                if (!AgentUUIDToName(pair.Value.ID, Configuration.SERVICES_TIMEOUT, ref agentName))
                                    continue;
                                csv.Add(agentName);
                                csv.Add(pair.Key.ToString());
                            }
                            agentInGroupEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupMembersReply += HandleGroupMembersReplyDelegate;
                            Client.Groups.RequestGroupMembers(groupUUID);
                            if (!agentInGroupEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupMembersReply -= HandleGroupMembersReplyDelegate;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_GROUP_MEMBERS));
                            }
                            Client.Groups.GroupMembersReply -= HandleGroupMembersReplyDelegate;
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETMEMBERROLES:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        if (!AgentInGroup(agentUUID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_IN_GROUP));
                        }
                        HashSet<string> csv = new HashSet<string>();
                        // get roles for a member
                        ManualResetEvent GroupRoleMembersReplyEvent = new ManualResetEvent(false);
                        EventHandler<GroupRolesMembersReplyEventArgs> GroupRolesMembersEventHandler = (sender, args) =>
                        {
                            foreach (
                                KeyValuePair<UUID, UUID> pair in args.RolesMembers.Where(o => o.Value.Equals(agentUUID))
                                )
                            {
                                string roleName = string.Empty;
                                if (
                                    !RoleUUIDToName(pair.Key, groupUUID, Configuration.SERVICES_TIMEOUT,
                                        ref roleName))
                                    continue;
                                csv.Add(roleName);
                            }
                            GroupRoleMembersReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupRoleMembersReply += GroupRolesMembersEventHandler;
                            Client.Groups.RequestGroupRolesMembers(groupUUID);
                            if (!GroupRoleMembersReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupRoleMembersReply -= GroupRolesMembersEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETING_GROUP_ROLES_MEMBERS));
                            }
                            Client.Groups.GroupRoleMembersReply -= GroupRolesMembersEventHandler;
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETROLEMEMBERS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(o => o.Name.Equals(group, StringComparison.Ordinal))
                                .UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (
                            !AgentInGroup(Client.Self.AgentID, groupUUID,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        string role =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ROLE),
                                message));
                        if (string.IsNullOrEmpty(role))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_ROLE_NAME_SPECIFIED));
                        }
                        List<string> csv = new List<string>();
                        ManualResetEvent GroupRoleMembersReplyEvent = new ManualResetEvent(false);
                        EventHandler<GroupRolesMembersReplyEventArgs> GroupRolesMembersEventHandler =
                            (sender, args) =>
                            {
                                foreach (KeyValuePair<UUID, UUID> pair in args.RolesMembers)
                                {
                                    string roleName = string.Empty;
                                    if (
                                        !RoleUUIDToName(pair.Key, groupUUID, Configuration.SERVICES_TIMEOUT,
                                            ref roleName))
                                        continue;
                                    if (!roleName.Equals(role))
                                        continue;
                                    string agentName = string.Empty;
                                    if (!AgentUUIDToName(pair.Value, Configuration.SERVICES_TIMEOUT, ref agentName))
                                        continue;
                                    csv.Add(agentName);
                                    csv.Add(pair.Value.ToString());
                                }
                                GroupRoleMembersReplyEvent.Set();
                            };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupRoleMembersReply += GroupRolesMembersEventHandler;
                            Client.Groups.RequestGroupRolesMembers(groupUUID);
                            if (!GroupRoleMembersReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupRoleMembersReply -= GroupRolesMembersEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETING_GROUP_ROLES_MEMBERS));
                            }
                            Client.Groups.GroupRoleMembersReply -= GroupRolesMembersEventHandler;
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETROLESMEMBERS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (
                            !AgentInGroup(Client.Self.AgentID, groupUUID,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        List<string> csv = new List<string>();
                        ManualResetEvent GroupRoleMembersReplyEvent = new ManualResetEvent(false);
                        EventHandler<GroupRolesMembersReplyEventArgs> GroupRolesMembersEventHandler =
                            (sender, args) =>
                            {
                                foreach (KeyValuePair<UUID, UUID> pair in args.RolesMembers)
                                {
                                    string roleName = string.Empty;
                                    if (
                                        !RoleUUIDToName(pair.Key, groupUUID, Configuration.SERVICES_TIMEOUT,
                                            ref roleName))
                                        continue;
                                    string agentName = string.Empty;
                                    if (!AgentUUIDToName(pair.Value, Configuration.SERVICES_TIMEOUT, ref agentName))
                                        continue;
                                    csv.Add(roleName);
                                    csv.Add(agentName);
                                    csv.Add(pair.Value.ToString());
                                }
                                GroupRoleMembersReplyEvent.Set();
                            };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupRoleMembersReply += GroupRolesMembersEventHandler;
                            Client.Groups.RequestGroupRolesMembers(groupUUID);
                            if (!GroupRoleMembersReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupRoleMembersReply -= GroupRolesMembersEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETING_GROUP_ROLES_MEMBERS));
                            }
                            Client.Groups.GroupRoleMembersReply -= GroupRolesMembersEventHandler;
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETROLEPOWERS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.RoleProperties,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        if (
                            !AgentInGroup(Client.Self.AgentID, groupUUID,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        string role =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ROLE),
                                message));
                        UUID roleUUID;
                        if (!UUID.TryParse(role, out roleUUID) && !RoleNameToRoleUUID(role, groupUUID,
                            Configuration.SERVICES_TIMEOUT, ref roleUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ROLE_NOT_FOUND));
                        }
                        List<string> csv = new List<string>();
                        ManualResetEvent GroupRoleDataReplyEvent = new ManualResetEvent(false);
                        EventHandler<GroupRolesDataReplyEventArgs> GroupRoleDataEventHandler = (sender, args) =>
                        {
                            GroupRole queryRole = args.Roles.Values.FirstOrDefault(o => o.ID.Equals(roleUUID));
                            csv.AddRange(typeof (GroupPowers).GetFields(BindingFlags.Public | BindingFlags.Static)
                                .Where(
                                    o =>
                                        !(((ulong) o.GetValue(null) &
                                           (ulong) queryRole.Powers)).Equals(0))
                                .Select(o => o.Name));
                            GroupRoleDataReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupRoleDataReply += GroupRoleDataEventHandler;
                            Client.Groups.RequestGroupRoles(groupUUID);
                            if (!GroupRoleDataReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupRoleDataReply -= GroupRoleDataEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_ROLE_POWERS));
                            }
                            Client.Groups.GroupRoleDataReply -= GroupRoleDataEventHandler;
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.DELETEROLE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        if (
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.DeleteRole,
                                Configuration.SERVICES_TIMEOUT) ||
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.RemoveMember,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        string role =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ROLE),
                                message));
                        UUID roleUUID;
                        if (!UUID.TryParse(role, out roleUUID) && !RoleNameToRoleUUID(role, groupUUID,
                            Configuration.SERVICES_TIMEOUT, ref roleUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ROLE_NOT_FOUND));
                        }
                        if (roleUUID.Equals(UUID.Zero))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.CANNOT_DELETE_THE_EVERYONE_ROLE));
                        }
                        OpenMetaverse.Group commandGroup = new OpenMetaverse.Group();
                        if (!RequestGroup(groupUUID, Configuration.SERVICES_TIMEOUT, ref commandGroup))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (commandGroup.OwnerRole.Equals(roleUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.CANNOT_REMOVE_OWNER_ROLE));
                        }
                        // remove member from role
                        ManualResetEvent GroupRoleMembersReplyEvent = new ManualResetEvent(false);
                        EventHandler<GroupRolesMembersReplyEventArgs> GroupRolesMembersEventHandler = (sender, args) =>
                        {
                            Parallel.ForEach(args.RolesMembers.Where(o => o.Key.Equals(roleUUID)),
                                o => Client.Groups.RemoveFromRole(groupUUID, roleUUID, o.Value));
                            GroupRoleMembersReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupRoleMembersReply += GroupRolesMembersEventHandler;
                            Client.Groups.RequestGroupRolesMembers(groupUUID);
                            if (!GroupRoleMembersReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupRoleMembersReply -= GroupRolesMembersEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_EJECTING_AGENT));
                            }
                            Client.Groups.GroupRoleMembersReply -= GroupRolesMembersEventHandler;
                        }
                        Client.Groups.DeleteRole(groupUUID, roleUUID);
                    };
                    break;
                case ScriptKeys.ADDTOROLE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        if (
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.AssignMember,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        string role =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ROLE),
                                message));
                        UUID roleUUID;
                        if (!UUID.TryParse(role, out roleUUID) && !RoleNameToRoleUUID(role, groupUUID,
                            Configuration.SERVICES_TIMEOUT, ref roleUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ROLE_NOT_FOUND));
                        }
                        if (roleUUID.Equals(UUID.Zero))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(
                                    ScriptError.GROUP_MEMBERS_ARE_BY_DEFAULT_IN_THE_EVERYONE_ROLE));
                        }
                        Client.Groups.AddToRole(groupUUID, roleUUID, agentUUID);
                    };
                    break;
                case ScriptKeys.DELETEFROMROLE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        if (
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.RemoveMember,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        string role =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ROLE),
                                message));
                        UUID roleUUID;
                        if (!UUID.TryParse(role, out roleUUID) && !RoleNameToRoleUUID(role, groupUUID,
                            Configuration.SERVICES_TIMEOUT, ref roleUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ROLE_NOT_FOUND));
                        }
                        if (roleUUID.Equals(UUID.Zero))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(
                                    ScriptError.CANNOT_DELETE_A_GROUP_MEMBER_FROM_THE_EVERYONE_ROLE));
                        }
                        OpenMetaverse.Group commandGroup = new OpenMetaverse.Group();
                        if (!RequestGroup(groupUUID, Configuration.SERVICES_TIMEOUT, ref commandGroup))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (commandGroup.OwnerRole.Equals(roleUUID))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.CANNOT_REMOVE_USER_FROM_OWNER_ROLE));
                        }
                        Client.Groups.RemoveFromRole(groupUUID, roleUUID,
                            agentUUID);
                    };
                    break;
                case ScriptKeys.TELL:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_TALK))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        switch (
                            wasGetEnumValueFromDescription<Entity>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ENTITY),
                                        message)).ToLowerInvariant()))
                        {
                            case Entity.AVATAR:
                                UUID agentUUID;
                                if (
                                    !UUID.TryParse(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                                message)), out agentUUID) && !AgentNameToUUID(
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                                            message)),
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME), message)),
                                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                                }
                                Client.Self.InstantMessage(agentUUID,
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                            message)));
                                break;
                            case Entity.GROUP:
                                UUID groupUUID =
                                    Configuration.GROUPS.FirstOrDefault(
                                        o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                                if (groupUUID.Equals(UUID.Zero) &&
                                    !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                                }
                                if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                                }
                                if (!Client.Self.GroupChatSessions.ContainsKey(groupUUID))
                                {
                                    if (
                                        !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.JoinChat,
                                            Configuration.SERVICES_TIMEOUT))
                                    {
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                    }
                                    bool succeeded = false;
                                    ManualResetEvent GroupChatJoinedEvent = new ManualResetEvent(false);
                                    EventHandler<GroupChatJoinedEventArgs> GroupChatJoinedEventHandler =
                                        (sender, args) =>
                                        {
                                            succeeded = args.Success;
                                            GroupChatJoinedEvent.Set();
                                        };
                                    lock (ServicesLock)
                                    {
                                        Client.Self.GroupChatJoined += GroupChatJoinedEventHandler;
                                        Client.Self.RequestJoinGroupChat(groupUUID);
                                        if (!GroupChatJoinedEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                        {
                                            Client.Self.GroupChatJoined -= GroupChatJoinedEventHandler;
                                            throw new Exception(
                                                wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_JOINING_GROUP_CHAT));
                                        }
                                        Client.Self.GroupChatJoined -= GroupChatJoinedEventHandler;
                                    }
                                    if (!succeeded)
                                    {
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_JOIN_GROUP_CHAT));
                                    }
                                }
                                Client.Self.InstantMessageGroup(groupUUID,
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                            message)));
                                break;
                            case Entity.LOCAL:
                                int chatChannel;
                                if (
                                    !int.TryParse(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.CHANNEL),
                                                message)),
                                        out chatChannel))
                                {
                                    chatChannel = 0;
                                }
                                FieldInfo chatTypeInfo = typeof (ChatType).GetFields(BindingFlags.Public |
                                                                                     BindingFlags.Static)
                                    .FirstOrDefault(
                                        o =>
                                            o.Name.Equals(
                                                wasUriUnescapeDataString(
                                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TYPE),
                                                        message)),
                                                StringComparison.Ordinal));
                                Client.Self.Chat(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                            message)),
                                    chatChannel,
                                    chatTypeInfo != null
                                        ? (ChatType)
                                            chatTypeInfo
                                                .GetValue(null)
                                        : ChatType.Normal);
                                break;
                            case Entity.ESTATE:
                                Client.Estate.EstateMessage(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                            message)));
                                break;
                            case Entity.REGION:
                                Client.Estate.SimulatorMessage(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                            message)));
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ENTITY));
                        }
                    };
                    break;
                case ScriptKeys.NOTICE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        if (
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.SendNotices,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        GroupNotice notice = new GroupNotice
                        {
                            Message =
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE), message)),
                            Subject =
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.SUBJECT), message)),
                            OwnerID = Client.Self.AgentID
                        };
                        string item =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ITEM),
                                message));
                        if (!string.IsNullOrEmpty(item) && !UUID.TryParse(item, out notice.AttachmentID))
                        {
                            InventoryBase inventoryBaseItem =
                                FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, item
                                    ).FirstOrDefault();
                            if (inventoryBaseItem == null)
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                            }
                            notice.AttachmentID = inventoryBaseItem.UUID;
                        }
                        Client.Groups.SendGroupNotice(groupUUID, notice);
                    };
                    break;
                case ScriptKeys.PAY:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group, (int) Permissions.PERMISSION_ECONOMY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        int amount;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.AMOUNT), message)),
                                out amount))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_PAY_AMOUNT));
                        }
                        if (amount.Equals(0))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_PAY_AMOUNT));
                        }
                        if (!UpdateBalance(Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_OBTAIN_MONEY_BALANCE));
                        }
                        if (Client.Self.Balance < amount)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INSUFFICIENT_FUNDS));
                        }
                        UUID targetUUID;
                        switch (
                            wasGetEnumValueFromDescription<Entity>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ENTITY),
                                        message)).ToLowerInvariant()))
                        {
                            case Entity.GROUP:
                                targetUUID =
                                    Configuration.GROUPS.FirstOrDefault(
                                        o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                                if (targetUUID.Equals(UUID.Zero) &&
                                    !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref targetUUID))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                                }
                                Client.Self.GiveGroupMoney(targetUUID, amount,
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.REASON),
                                            message)));
                                break;
                            case Entity.AVATAR:
                                if (
                                    !UUID.TryParse(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                                message)), out targetUUID) && !AgentNameToUUID(
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                                            message)),
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME), message)),
                                                    Configuration.SERVICES_TIMEOUT, ref targetUUID))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                                }
                                Client.Self.GiveAvatarMoney(targetUUID, amount,
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.REASON),
                                            message)));
                                break;
                            case Entity.OBJECT:
                                if (
                                    !UUID.TryParse(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TARGET),
                                                message)),
                                        out targetUUID))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_PAY_TARGET));
                                }
                                Client.Self.GiveObjectMoney(targetUUID, amount,
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.REASON),
                                            message)));
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ENTITY));
                        }
                    };
                    break;
                case ScriptKeys.GETBALANCE:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group, (int) Permissions.PERMISSION_ECONOMY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        if (!UpdateBalance(Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_OBTAIN_MONEY_BALANCE));
                        }
                        result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                            Client.Self.Balance.ToString(CultureInfo.InvariantCulture));
                    };
                    break;
                case ScriptKeys.TELEPORT:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string region =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.REGION),
                                message));
                        if (string.IsNullOrEmpty(region))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_REGION_SPECIFIED));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        ManualResetEvent TeleportEvent = new ManualResetEvent(false);
                        bool succeeded = false;
                        EventHandler<TeleportEventArgs> TeleportEventHandler = (sender, args) =>
                        {
                            switch (args.Status)
                            {
                                case TeleportStatus.Finished:
                                    succeeded = Client.Network.CurrentSim.Name.Equals(region, StringComparison.Ordinal);
                                    TeleportEvent.Set();
                                    break;
                            }
                        };
                        if (Client.Self.Movement.SitOnGround || !Client.Self.SittingOn.Equals(0))
                        {
                            Client.Self.Stand();
                        }
                        Client.Self.SignaledAnimations.ForEach(
                            animation => Client.Self.AnimationStop(animation.Key, true));
                        lock (TeleportLock)
                        {
                            Client.Self.TeleportProgress += TeleportEventHandler;
                            Client.Self.Teleport(region, position);

                            if (!TeleportEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Self.TeleportProgress -= TeleportEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_DURING_TELEPORT));
                            }
                            Client.Self.TeleportProgress -= TeleportEventHandler;
                        }
                        if (!succeeded)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TELEPORT_FAILED));
                        }
                    };
                    break;
                case ScriptKeys.LURE:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        Client.Self.SendTeleportLure(agentUUID,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                message)));
                    };
                    break;
                case ScriptKeys.SETHOME:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        bool succeeded = true;
                        ManualResetEvent AlertMessageEvent = new ManualResetEvent(false);
                        EventHandler<AlertMessageEventArgs> AlertMessageEventHandler = (sender, args) =>
                        {
                            switch (args.Message)
                            {
                                case LINDEN_CONSTANTS.ALERTS.UNABLE_TO_SET_HOME:
                                    succeeded = false;
                                    AlertMessageEvent.Set();
                                    break;
                                case LINDEN_CONSTANTS.ALERTS.HOME_SET:
                                    succeeded = true;
                                    AlertMessageEvent.Set();
                                    break;
                            }
                        };
                        lock (ServicesLock)
                        {
                            Client.Self.AlertMessage += AlertMessageEventHandler;
                            Client.Self.SetHome();
                            if (!AlertMessageEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Self.AlertMessage -= AlertMessageEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_REQUESTING_TO_SET_HOME));
                            }
                            Client.Self.AlertMessage -= AlertMessageEventHandler;
                        }
                        if (!succeeded)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_SET_HOME));
                        }
                    };
                    break;
                case ScriptKeys.GOHOME:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        if (Client.Self.Movement.SitOnGround || !Client.Self.SittingOn.Equals(0))
                        {
                            Client.Self.Stand();
                        }
                        Client.Self.SignaledAnimations.ForEach(
                            animation => Client.Self.AnimationStop(animation.Key, true));
                        bool succeeded;
                        lock (TeleportLock)
                        {
                            succeeded = Client.Self.GoHome();
                        }
                        if (!succeeded)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_GO_HOME));
                        }
                    };
                    break;
                case ScriptKeys.GETREGIONDATA:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        List<string> data = new List<string>(GetStructuredData(Client.Network.CurrentSim,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message)))
                            );
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETGRIDREGIONDATA:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string region =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.REGION),
                                message));
                        if (string.IsNullOrEmpty(region))
                        {
                            region = Client.Network.CurrentSim.Name;
                        }
                        ManualResetEvent GridRegionEvent = new ManualResetEvent(false);
                        GridRegion gridRegion = new GridRegion();
                        EventHandler<GridRegionEventArgs> GridRegionEventHandler = (sender, args) =>
                        {
                            gridRegion = args.Region;
                            GridRegionEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Grid.GridRegion += GridRegionEventHandler;
                            Client.Grid.RequestMapRegion(region, GridLayerType.Objects);
                            if (!GridRegionEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Grid.GridRegion -= GridRegionEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_REGION));
                            }
                            Client.Grid.GridRegion -= GridRegionEventHandler;
                        }
                        List<string> data = new List<string>(GetStructuredData(gridRegion,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message))));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.SIT:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        ManualResetEvent SitEvent = new ManualResetEvent(false);
                        bool succeeded = false;
                        EventHandler<AvatarSitResponseEventArgs> AvatarSitEventHandler = (sender, args) =>
                        {
                            succeeded = !args.ObjectID.Equals(UUID.Zero);
                            SitEvent.Set();
                        };
                        EventHandler<AlertMessageEventArgs> AlertMessageEventHandler = (sender, args) =>
                        {
                            if (args.Message.Equals(LINDEN_CONSTANTS.ALERTS.NO_ROOM_TO_SIT_HERE))
                            {
                                succeeded = false;
                            }
                            SitEvent.Set();
                        };
                        if (Client.Self.Movement.SitOnGround || !Client.Self.SittingOn.Equals(0))
                        {
                            Client.Self.Stand();
                        }
                        Client.Self.SignaledAnimations.ForEach(
                            animation => Client.Self.AnimationStop(animation.Key, true));
                        lock (ServicesLock)
                        {
                            Client.Self.AvatarSitResponse += AvatarSitEventHandler;
                            Client.Self.AlertMessage += AlertMessageEventHandler;
                            Client.Self.RequestSit(primitive.ID, Vector3.Zero);
                            if (!SitEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Self.AvatarSitResponse -= AvatarSitEventHandler;
                                Client.Self.AlertMessage -= AlertMessageEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_REQUESTING_SIT));
                            }
                            Client.Self.AvatarSitResponse -= AvatarSitEventHandler;
                            Client.Self.AlertMessage -= AlertMessageEventHandler;
                        }
                        if (!succeeded)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_SIT));
                        }
                        Client.Self.Sit();
                    };
                    break;
                case ScriptKeys.STAND:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        if (Client.Self.Movement.SitOnGround || !Client.Self.SittingOn.Equals(0))
                        {
                            Client.Self.Stand();
                        }
                        Client.Self.SignaledAnimations.ForEach(
                            animation => Client.Self.AnimationStop(animation.Key, true));
                    };
                    break;
                case ScriptKeys.GETPARCELLIST:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(o => o.Name.Equals(group, StringComparison.Ordinal))
                                .UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        Parcel parcel = null;
                        if (
                            !GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                        }
                        FieldInfo accessField = typeof (AccessList).GetFields(
                            BindingFlags.Public | BindingFlags.Static)
                            .FirstOrDefault(
                                o =>
                                    o.Name.Equals(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TYPE),
                                                message)),
                                        StringComparison.Ordinal));
                        if (accessField == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ACCESS_LIST_TYPE));
                        }
                        AccessList accessType = (AccessList) accessField.GetValue(null);
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            if (!parcel.OwnerID.Equals(Client.Self.AgentID))
                            {
                                if (!parcel.IsGroupOwned && !parcel.GroupID.Equals(groupUUID))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                }
                                switch (accessType)
                                {
                                    case AccessList.Access:
                                        if (
                                            !HasGroupPowers(Client.Self.AgentID, groupUUID,
                                                GroupPowers.LandManageAllowed, Configuration.SERVICES_TIMEOUT))
                                        {
                                            throw new Exception(
                                                wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                        }
                                        break;
                                    case AccessList.Ban:
                                        if (
                                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.LandManageBanned,
                                                Configuration.SERVICES_TIMEOUT))
                                        {
                                            throw new Exception(
                                                wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                        }
                                        break;
                                    case AccessList.Both:
                                        if (
                                            !HasGroupPowers(Client.Self.AgentID, groupUUID,
                                                GroupPowers.LandManageAllowed, Configuration.SERVICES_TIMEOUT))
                                        {
                                            throw new Exception(
                                                wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                        }
                                        if (
                                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.LandManageBanned,
                                                Configuration.SERVICES_TIMEOUT))
                                        {
                                            throw new Exception(
                                                wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                        }
                                        break;
                                }
                            }
                        }
                        List<string> csv = new List<string>();
                        ManualResetEvent ParcelAccessListEvent = new ManualResetEvent(false);
                        EventHandler<ParcelAccessListReplyEventArgs> ParcelAccessListHandler = (sender, args) =>
                        {
                            foreach (ParcelManager.ParcelAccessEntry parcelAccess in args.AccessList)
                            {
                                string agent = string.Empty;
                                if (!AgentUUIDToName(parcelAccess.AgentID, Configuration.SERVICES_TIMEOUT, ref agent))
                                    continue;
                                csv.Add(agent);
                                csv.Add(parcelAccess.AgentID.ToString());
                                csv.Add(parcelAccess.Flags.ToString());
                                csv.Add(parcelAccess.Time.ToString(CultureInfo.InvariantCulture));
                            }
                            ParcelAccessListEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Parcels.ParcelAccessListReply += ParcelAccessListHandler;
                            Client.Parcels.RequestParcelAccessList(Client.Network.CurrentSim, parcel.LocalID, accessType,
                                0);
                            if (!ParcelAccessListEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Parcels.ParcelAccessListReply -= ParcelAccessListHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_PARCELS));
                            }
                            Client.Parcels.ParcelAccessListReply -= ParcelAccessListHandler;
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.PARCELRECLAIM:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        Parcel parcel = null;
                        if (
                            !GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                        }
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_LAND_RIGHTS));
                        }
                        Client.Parcels.Reclaim(Client.Network.CurrentSim, parcel.LocalID);
                    };
                    break;
                case ScriptKeys.PARCELRELEASE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(o => o.Name.Equals(group, StringComparison.Ordinal))
                                .UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        Parcel parcel = null;
                        if (
                            !GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                        }
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            if (!parcel.OwnerID.Equals(Client.Self.AgentID))
                            {
                                if (!parcel.IsGroupOwned && !parcel.GroupID.Equals(groupUUID))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                }
                                if (
                                    !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.LandRelease,
                                        Configuration.SERVICES_TIMEOUT))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                }
                            }
                        }
                        Client.Parcels.ReleaseParcel(Client.Network.CurrentSim, parcel.LocalID);
                    };
                    break;
                case ScriptKeys.PARCELDEED:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(o => o.Name.Equals(group, StringComparison.Ordinal))
                                .UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        Parcel parcel = null;
                        if (
                            !GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                        }
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            if (!parcel.OwnerID.Equals(Client.Self.AgentID))
                            {
                                if (!parcel.IsGroupOwned && !parcel.GroupID.Equals(groupUUID))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                }
                            }
                        }
                        if (
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.LandDeed,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        Client.Parcels.DeedToGroup(Client.Network.CurrentSim, parcel.LocalID, groupUUID);
                    };
                    break;
                case ScriptKeys.PARCELBUY:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        Parcel parcel = null;
                        if (
                            !GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                        }
                        bool forGroup;
                        if (
                            !bool.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FORGROUP), message)),
                                out forGroup))
                        {
                            if (
                                !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.LandDeed,
                                    Configuration.SERVICES_TIMEOUT))
                            {
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                            }
                            forGroup = true;
                        }
                        bool removeContribution;
                        if (!bool.TryParse(
                            wasUriUnescapeDataString(
                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.REMOVECONTRIBUTION),
                                    message)),
                            out removeContribution))
                        {
                            removeContribution = true;
                        }
                        ManualResetEvent ParcelInfoEvent = new ManualResetEvent(false);
                        UUID parcelUUID = UUID.Zero;
                        EventHandler<ParcelInfoReplyEventArgs> ParcelInfoEventHandler = (sender, args) =>
                        {
                            parcelUUID = args.Parcel.ID;
                            ParcelInfoEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Parcels.ParcelInfoReply += ParcelInfoEventHandler;
                            Client.Parcels.RequestParcelInfo(parcelUUID);
                            if (!ParcelInfoEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Parcels.ParcelInfoReply -= ParcelInfoEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_PARCELS));
                            }
                            Client.Parcels.ParcelInfoReply -= ParcelInfoEventHandler;
                        }
                        bool forSale = false;
                        int handledEvents = 0;
                        int counter = 1;
                        ManualResetEvent DirLandReplyEvent = new ManualResetEvent(false);
                        EventHandler<DirLandReplyEventArgs> DirLandReplyEventArgs =
                            (sender, args) =>
                            {
                                handledEvents += args.DirParcels.Count;
                                Parallel.ForEach(args.DirParcels, o =>
                                {
                                    if (o.ID.Equals(parcelUUID))
                                    {
                                        forSale = o.ForSale;
                                        DirLandReplyEvent.Set();
                                    }
                                });
                                if (((handledEvents - counter)%
                                     LINDEN_CONSTANTS.DIRECTORY.LAND.SEARCH_RESULTS_COUNT).Equals(0))
                                {
                                    ++counter;
                                    Client.Directory.StartLandSearch(DirectoryManager.DirFindFlags.SortAsc,
                                        DirectoryManager.SearchTypeFlags.Any, int.MaxValue, int.MaxValue,
                                        handledEvents);
                                }
                                DirLandReplyEvent.Set();
                            };
                        lock (ServicesLock)
                        {
                            Client.Directory.DirLandReply += DirLandReplyEventArgs;
                            Client.Directory.StartLandSearch(DirectoryManager.DirFindFlags.SortAsc,
                                DirectoryManager.SearchTypeFlags.Any, int.MaxValue, int.MaxValue, handledEvents);
                            if (!DirLandReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Directory.DirLandReply -= DirLandReplyEventArgs;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_PARCELS));
                            }
                            Client.Directory.DirLandReply -= DirLandReplyEventArgs;
                        }
                        if (!forSale)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PARCEL_NOT_FOR_SALE));
                        }
                        if (!UpdateBalance(Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_OBTAIN_MONEY_BALANCE));
                        }
                        if (Client.Self.Balance < parcel.SalePrice)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INSUFFICIENT_FUNDS));
                        }
                        Client.Parcels.Buy(Client.Network.CurrentSim, parcel.LocalID, forGroup, groupUUID,
                            removeContribution, parcel.Area, parcel.SalePrice);
                    };
                    break;
                case ScriptKeys.PARCELEJECT:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        Parcel parcel = null;
                        if (
                            !GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                        }
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            if (!parcel.OwnerID.Equals(Client.Self.AgentID))
                            {
                                if (!parcel.IsGroupOwned && !parcel.GroupID.Equals(groupUUID))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                }
                                if (!HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.LandEjectAndFreeze,
                                    Configuration.SERVICES_TIMEOUT))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                }
                            }
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        bool alsoban;
                        if (
                            !bool.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.BAN),
                                    message)),
                                out alsoban))
                        {
                            alsoban = false;
                        }
                        Client.Parcels.EjectUser(agentUUID, alsoban);
                    };
                    break;
                case ScriptKeys.PARCELFREEZE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        Parcel parcel = null;
                        if (
                            !GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                        }
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            if (!parcel.OwnerID.Equals(Client.Self.AgentID))
                            {
                                if (!parcel.IsGroupOwned && !parcel.GroupID.Equals(groupUUID))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                }
                                if (!HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.LandEjectAndFreeze,
                                    Configuration.SERVICES_TIMEOUT))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                }
                            }
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        bool freeze;
                        if (
                            !bool.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FREEZE), message)),
                                out freeze))
                        {
                            freeze = false;
                        }
                        Client.Parcels.FreezeUser(agentUUID, freeze);
                    };
                    break;
                case ScriptKeys.SETPROFILEDATA:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        ManualResetEvent[] AvatarProfileDataEvent =
                        {
                            new ManualResetEvent(false),
                            new ManualResetEvent(false)
                        };
                        Avatar.AvatarProperties properties = new Avatar.AvatarProperties();
                        Avatar.Interests interests = new Avatar.Interests();
                        EventHandler<AvatarPropertiesReplyEventArgs> AvatarPropertiesEventHandler = (sender, args) =>
                        {
                            properties = args.Properties;
                            AvatarProfileDataEvent[0].Set();
                        };
                        EventHandler<AvatarInterestsReplyEventArgs> AvatarInterestsEventHandler = (sender, args) =>
                        {
                            interests = args.Interests;
                            AvatarProfileDataEvent[1].Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Avatars.AvatarPropertiesReply += AvatarPropertiesEventHandler;
                            Client.Avatars.AvatarInterestsReply += AvatarInterestsEventHandler;
                            Client.Avatars.RequestAvatarProperties(Client.Self.AgentID);
                            if (
                                !WaitHandle.WaitAll(AvatarProfileDataEvent.Select(o => (WaitHandle) o).ToArray(),
                                    Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Avatars.AvatarPropertiesReply -= AvatarPropertiesEventHandler;
                                Client.Avatars.AvatarInterestsReply -= AvatarInterestsEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_PROFILE));
                            }
                            Client.Avatars.AvatarPropertiesReply -= AvatarPropertiesEventHandler;
                            Client.Avatars.AvatarInterestsReply -= AvatarInterestsEventHandler;
                        }
                        string fields =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message));
                        wasCSVToStructure(fields, ref properties);
                        wasCSVToStructure(fields, ref interests);
                        Client.Self.UpdateProfile(properties);
                        Client.Self.UpdateInterests(interests);
                    };
                    break;
                case ScriptKeys.GETPROFILEDATA:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        ManualResetEvent[] AvatarProfileDataEvent =
                        {
                            new ManualResetEvent(false),
                            new ManualResetEvent(false)
                        };
                        Avatar.AvatarProperties properties = new Avatar.AvatarProperties();
                        Avatar.Interests interests = new Avatar.Interests();
                        EventHandler<AvatarPropertiesReplyEventArgs> AvatarPropertiesEventHandler = (sender, args) =>
                        {
                            properties = args.Properties;
                            AvatarProfileDataEvent[0].Set();
                        };
                        EventHandler<AvatarInterestsReplyEventArgs> AvatarInterestsEventHandler = (sender, args) =>
                        {
                            interests = args.Interests;
                            AvatarProfileDataEvent[1].Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Avatars.AvatarPropertiesReply += AvatarPropertiesEventHandler;
                            Client.Avatars.AvatarInterestsReply += AvatarInterestsEventHandler;
                            Client.Avatars.RequestAvatarProperties(agentUUID);
                            if (
                                !WaitHandle.WaitAll(AvatarProfileDataEvent.Select(o => (WaitHandle) o).ToArray(),
                                    Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Avatars.AvatarPropertiesReply -= AvatarPropertiesEventHandler;
                                Client.Avatars.AvatarInterestsReply -= AvatarInterestsEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_PROFILE));
                            }
                            Client.Avatars.AvatarPropertiesReply -= AvatarPropertiesEventHandler;
                            Client.Avatars.AvatarInterestsReply -= AvatarInterestsEventHandler;
                        }
                        string fields =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message));
                        List<string> csv = new List<string>();
                        csv.AddRange(GetStructuredData(properties, fields));
                        csv.AddRange(GetStructuredData(interests, fields));
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GIVE:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_INVENTORY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        InventoryBase inventoryBaseItem =
                            FindInventory<InventoryBase>(Client.Inventory.Store.RootNode,
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message))
                                ).FirstOrDefault();
                        if (inventoryBaseItem == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                        }
                        switch (
                            wasGetEnumValueFromDescription<Entity>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ENTITY),
                                        message)).ToLowerInvariant()))
                        {
                            case Entity.AVATAR:
                                UUID agentUUID;
                                if (
                                    !UUID.TryParse(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                                message)), out agentUUID) && !AgentNameToUUID(
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                                            message)),
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME), message)),
                                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                                }
                                InventoryItem inventoryItem = inventoryBaseItem as InventoryItem;
                                if (inventoryItem != null)
                                {
                                    Client.Inventory.GiveItem(inventoryBaseItem.UUID, inventoryBaseItem.Name,
                                        inventoryItem.AssetType, agentUUID, true);
                                }
                                break;
                            case Entity.OBJECT:
                                float range;
                                if (
                                    !float.TryParse(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.RANGE),
                                                message)),
                                        out range))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                                }
                                Primitive primitive = null;
                                if (
                                    !FindPrimitive(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TARGET),
                                                message)),
                                        range,
                                        Configuration.SERVICES_TIMEOUT,
                                        ref primitive))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                                }
                                Client.Inventory.UpdateTaskInventory(primitive.LocalID,
                                    inventoryBaseItem as InventoryItem);
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ENTITY));
                        }
                    };
                    break;
                case ScriptKeys.DELETEITEM:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_INVENTORY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        HashSet<InventoryItem> items =
                            new HashSet<InventoryItem>(FindInventory<InventoryBase>(Client.Inventory.Store.RootNode,
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message))
                                ).Cast<InventoryItem>());
                        if (items.Count.Equals(0))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                        }
                        Parallel.ForEach(items, o =>
                        {
                            switch (o.AssetType)
                            {
                                case AssetType.Folder:
                                    Client.Inventory.MoveFolder(o.UUID,
                                        Client.Inventory.FindFolderForType(AssetType.TrashFolder));
                                    break;
                                default:
                                    Client.Inventory.MoveItem(o.UUID,
                                        Client.Inventory.FindFolderForType(AssetType.TrashFolder));
                                    break;
                            }
                        });
                    };
                    break;
                case ScriptKeys.EMPTYTRASH:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_INVENTORY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        Client.Inventory.EmptyTrash();
                    };
                    break;
                case ScriptKeys.FLY:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        uint action =
                            (uint) wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION), message))
                                    .ToLowerInvariant());
                        switch ((Action) action)
                        {
                            case Action.START:
                            case Action.STOP:
                                if (Client.Self.Movement.SitOnGround || !Client.Self.SittingOn.Equals(0))
                                {
                                    Client.Self.Stand();
                                }
                                Client.Self.SignaledAnimations.ForEach(
                                    o => Client.Self.AnimationStop(o.Key, true));
                                Client.Self.Fly(action.Equals((uint) Action.START));
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.FLY_ACTION_START_OR_STOP));
                        }
                    };
                    break;
                case ScriptKeys.ADDPICK:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string item =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ITEM),
                                message));
                        UUID textureUUID = UUID.Zero;
                        if (!string.IsNullOrEmpty(item) && !UUID.TryParse(item, out textureUUID))
                        {
                            InventoryBase inventoryBaseItem =
                                FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, item
                                    ).FirstOrDefault();
                            if (inventoryBaseItem == null)
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TEXTURE_NOT_FOUND));
                            }
                            textureUUID = inventoryBaseItem.UUID;
                        }
                        ManualResetEvent AvatarPicksReplyEvent = new ManualResetEvent(false);
                        UUID pickUUID = UUID.Zero;
                        string pickName =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.NAME),
                                message));
                        if (string.IsNullOrEmpty(pickName))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.EMPTY_PICK_NAME));
                        }
                        EventHandler<AvatarPicksReplyEventArgs> AvatarPicksEventHandler = (sender, args) =>
                        {
                            pickUUID =
                                args.Picks.FirstOrDefault(
                                    o => o.Value.Equals(pickName, StringComparison.Ordinal)).Key;
                            AvatarPicksReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Avatars.AvatarPicksReply += AvatarPicksEventHandler;
                            Client.Avatars.RequestAvatarPicks(Client.Self.AgentID);
                            if (!AvatarPicksReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Avatars.AvatarPicksReply -= AvatarPicksEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_PICKS));
                            }
                            Client.Avatars.AvatarPicksReply -= AvatarPicksEventHandler;
                        }
                        if (pickUUID.Equals(UUID.Zero))
                        {
                            pickUUID = UUID.Random();
                        }
                        Client.Self.PickInfoUpdate(pickUUID, false, UUID.Zero, pickName,
                            Client.Self.GlobalPosition, textureUUID,
                            wasUriUnescapeDataString(
                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DESCRIPTION), message)));
                    };
                    break;
                case ScriptKeys.DELETEPICK:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        ManualResetEvent AvatarPicksReplyEvent = new ManualResetEvent(false);
                        string pickName =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.NAME),
                                message));
                        if (string.IsNullOrEmpty(pickName))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.EMPTY_PICK_NAME));
                        }
                        UUID pickUUID = UUID.Zero;
                        EventHandler<AvatarPicksReplyEventArgs> AvatarPicksEventHandler = (sender, args) =>
                        {
                            pickUUID =
                                args.Picks.FirstOrDefault(
                                    o => o.Value.Equals(pickName, StringComparison.Ordinal)).Key;
                            AvatarPicksReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Avatars.AvatarPicksReply += AvatarPicksEventHandler;
                            Client.Avatars.RequestAvatarPicks(Client.Self.AgentID);
                            if (!AvatarPicksReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Avatars.AvatarPicksReply -= AvatarPicksEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_PICKS));
                            }
                            Client.Avatars.AvatarPicksReply -= AvatarPicksEventHandler;
                        }
                        if (pickUUID.Equals(UUID.Zero))
                        {
                            pickUUID = UUID.Random();
                        }
                        Client.Self.PickDelete(pickUUID);
                    };
                    break;
                case ScriptKeys.ADDCLASSIFIED:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string item =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ITEM),
                                message));
                        UUID textureUUID = UUID.Zero;
                        if (!string.IsNullOrEmpty(item) && !UUID.TryParse(item, out textureUUID))
                        {
                            InventoryBase inventoryBaseItem =
                                FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, item
                                    ).FirstOrDefault();
                            if (inventoryBaseItem == null)
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TEXTURE_NOT_FOUND));
                            }
                            textureUUID = inventoryBaseItem.UUID;
                        }
                        string classifiedName =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.NAME),
                                message));
                        if (string.IsNullOrEmpty(classifiedName))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.EMPTY_CLASSIFIED_NAME));
                        }
                        string classifiedDescription =
                            wasUriUnescapeDataString(
                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DESCRIPTION), message));
                        ManualResetEvent AvatarClassifiedReplyEvent = new ManualResetEvent(false);
                        UUID classifiedUUID = UUID.Zero;
                        EventHandler<AvatarClassifiedReplyEventArgs> AvatarClassifiedEventHandler = (sender, args) =>
                        {
                            classifiedUUID =
                                args.Classifieds.FirstOrDefault(
                                    o =>
                                        o.Value.Equals(classifiedName, StringComparison.Ordinal)).Key;
                            AvatarClassifiedReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Avatars.AvatarClassifiedReply += AvatarClassifiedEventHandler;
                            Client.Avatars.RequestAvatarClassified(Client.Self.AgentID);
                            if (!AvatarClassifiedReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Avatars.AvatarClassifiedReply -= AvatarClassifiedEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_CLASSIFIEDS));
                            }
                            Client.Avatars.AvatarClassifiedReply -= AvatarClassifiedEventHandler;
                        }
                        if (classifiedUUID.Equals(UUID.Zero))
                        {
                            classifiedUUID = UUID.Random();
                        }
                        int price;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.PRICE), message)),
                                out price))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_PRICE));
                        }
                        if (price < 0)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_PRICE));
                        }
                        bool renew;
                        if (
                            !bool.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RENEW), message)),
                                out renew))
                        {
                            renew = false;
                        }
                        FieldInfo classifiedCategoriesField = typeof (DirectoryManager.ClassifiedCategories).GetFields(
                            BindingFlags.Public |
                            BindingFlags.Static)
                            .FirstOrDefault(o =>
                                o.Name.Equals(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TYPE), message)),
                                    StringComparison.Ordinal));
                        Client.Self.UpdateClassifiedInfo(classifiedUUID, classifiedCategoriesField != null
                            ? (DirectoryManager.ClassifiedCategories)
                                classifiedCategoriesField.GetValue(null)
                            : DirectoryManager.ClassifiedCategories.Any, textureUUID, price,
                            classifiedName, classifiedDescription, renew);
                    };
                    break;
                case ScriptKeys.DELETECLASSIFIED:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string classifiedName =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.NAME),
                                message));
                        if (string.IsNullOrEmpty(classifiedName))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.EMPTY_CLASSIFIED_NAME));
                        }
                        ManualResetEvent AvatarClassifiedReplyEvent = new ManualResetEvent(false);
                        UUID classifiedUUID = UUID.Zero;
                        EventHandler<AvatarClassifiedReplyEventArgs> AvatarClassifiedEventHandler = (sender, args) =>
                        {
                            classifiedUUID =
                                args.Classifieds.FirstOrDefault(
                                    o =>
                                        o.Value.Equals(classifiedName, StringComparison.Ordinal)).Key;
                            AvatarClassifiedReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Avatars.AvatarClassifiedReply += AvatarClassifiedEventHandler;
                            Client.Avatars.RequestAvatarClassified(Client.Self.AgentID);
                            if (!AvatarClassifiedReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Avatars.AvatarClassifiedReply -= AvatarClassifiedEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_CLASSIFIEDS));
                            }
                            Client.Avatars.AvatarClassifiedReply -= AvatarClassifiedEventHandler;
                        }
                        if (classifiedUUID.Equals(UUID.Zero))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_CLASSIFIED));
                        }
                        Client.Self.DeleteClassfied(classifiedUUID);
                    };
                    break;
                case ScriptKeys.TOUCH:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        Client.Self.Touch(primitive.LocalID);
                    };
                    break;
                case ScriptKeys.MODERATE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.ModerateChat,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        if (!AgentInGroup(agentUUID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_IN_GROUP));
                        }
                        bool silence;
                        if (
                            !bool.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.SILENCE), message)),
                                out silence))
                        {
                            silence = false;
                        }
                        uint type =
                            (uint) wasGetEnumValueFromDescription<Type>(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.TYPE), message))
                                    .ToLowerInvariant());
                        switch ((Type) type)
                        {
                            case Type.TEXT:
                            case Type.VOICE:
                                Client.Self.ModerateChatSessions(groupUUID, agentUUID,
                                    wasGetDescriptionFromEnumValue((Type) type),
                                    silence);
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TYPE_CAN_BE_VOICE_OR_TEXT));
                        }
                    };
                    break;
                case ScriptKeys.REBAKE:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        if (!Rebake(Configuration.SERVICES_TIMEOUT, Configuration.REBAKE_DELAY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.REBAKE_FAILED));
                        }
                    };
                    break;
                case ScriptKeys.GETWEARABLES:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        List<string> data =
                            new List<string>(GetWearables(Client.Inventory.Store.RootNode)
                                .Select(o => new[]
                                {
                                    o.Key.ToString(),
                                    o.Value
                                }).SelectMany(o => o));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.WEAR:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string wearables =
                            wasUriUnescapeDataString(wasKeyValueGet(
                                wasGetDescriptionFromEnumValue(ScriptKeys.WEARABLES), message));
                        if (string.IsNullOrEmpty(wearables))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.EMPTY_WEARABLES));
                        }
                        bool replace;
                        if (
                            !bool.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.REPLACE), message)),
                                out replace))
                        {
                            replace = true;
                        }
                        Parallel.ForEach(
                            wearables.Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER},
                                StringSplitOptions.RemoveEmptyEntries), o =>
                                {
                                    InventoryBase inventoryBaseItem =
                                        FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, o
                                            ).FirstOrDefault(p => p is InventoryWearable);
                                    if (inventoryBaseItem == null)
                                        return;
                                    Wear(inventoryBaseItem as InventoryItem, replace, Configuration.SERVICES_TIMEOUT);
                                });
                        if (!Rebake(Configuration.SERVICES_TIMEOUT, Configuration.REBAKE_DELAY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.REBAKE_FAILED));
                        }
                    };
                    break;
                case ScriptKeys.UNWEAR:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string wearables =
                            wasUriUnescapeDataString(wasKeyValueGet(
                                wasGetDescriptionFromEnumValue(ScriptKeys.WEARABLES), message));
                        if (string.IsNullOrEmpty(wearables))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.EMPTY_WEARABLES));
                        }
                        Parallel.ForEach(
                            wearables.Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER},
                                StringSplitOptions.RemoveEmptyEntries), o =>
                                {
                                    InventoryBase inventoryBaseItem =
                                        FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, o
                                            ).FirstOrDefault(p => p is InventoryWearable);
                                    if (inventoryBaseItem == null)
                                        return;
                                    UnWear(inventoryBaseItem as InventoryItem, Configuration.SERVICES_TIMEOUT);
                                });
                        if (!Rebake(Configuration.SERVICES_TIMEOUT, Configuration.REBAKE_DELAY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.REBAKE_FAILED));
                        }
                    };
                    break;
                case ScriptKeys.GETATTACHMENTS:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        List<string> attachments = GetAttachments(
                            Configuration.SERVICES_TIMEOUT).Select(o => new[]
                            {
                                o.Value.ToString(),
                                o.Key.Properties.Name
                            }).SelectMany(o => o).ToList();
                        if (!attachments.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, attachments.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.ATTACH:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string attachments =
                            wasUriUnescapeDataString(
                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ATTACHMENTS), message));
                        if (string.IsNullOrEmpty(attachments))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.EMPTY_ATTACHMENTS));
                        }
                        bool replace;
                        if (
                            !bool.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.REPLACE), message)),
                                out replace))
                        {
                            replace = true;
                        }
                        Parallel.ForEach(Regex.Matches(attachments, @"\s*(?<key>.+?)\s*,\s*(?<value>.+?)\s*(,|$)",
                            RegexOptions.Compiled)
                            .Cast<Match>()
                            .ToDictionary(o => o.Groups["key"].Value, o => o.Groups["value"].Value),
                            o =>
                                Parallel.ForEach(
                                    typeof (AttachmentPoint).GetFields(BindingFlags.Public | BindingFlags.Static)
                                        .Where(
                                            p =>
                                                p.Name.Equals(o.Key, StringComparison.Ordinal)),
                                    q =>
                                    {
                                        InventoryBase inventoryBaseItem =
                                            FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, o.Value
                                                )
                                                .FirstOrDefault(
                                                    r => r is InventoryObject || r is InventoryAttachment);
                                        if (inventoryBaseItem == null)
                                            return;
                                        Attach(inventoryBaseItem as InventoryItem, (AttachmentPoint) q.GetValue(null),
                                            replace, Configuration.SERVICES_TIMEOUT);
                                    }));
                        if (!Rebake(Configuration.SERVICES_TIMEOUT, Configuration.REBAKE_DELAY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.REBAKE_FAILED));
                        }
                    };
                    break;
                case ScriptKeys.DETACH:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string attachments =
                            wasUriUnescapeDataString(
                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ATTACHMENTS), message));
                        if (string.IsNullOrEmpty(attachments))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.EMPTY_ATTACHMENTS));
                        }
                        Parallel.ForEach(
                            attachments.Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER},
                                StringSplitOptions.RemoveEmptyEntries), o =>
                                {
                                    InventoryBase inventoryBaseItem =
                                        FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, o
                                            )
                                            .FirstOrDefault(
                                                p =>
                                                    p is InventoryObject || p is InventoryAttachment);
                                    if (inventoryBaseItem == null)
                                        return;
                                    Detach(inventoryBaseItem as InventoryItem, Configuration.SERVICES_TIMEOUT);
                                });
                        if (!Rebake(Configuration.SERVICES_TIMEOUT, Configuration.REBAKE_DELAY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.REBAKE_FAILED));
                        }
                    };
                    break;
                case ScriptKeys.RETURNPRIMITIVES:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        string type =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TYPE),
                                message));
                        switch (
                            wasGetEnumValueFromDescription<Entity>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ENTITY),
                                        message)).ToLowerInvariant()))
                        {
                            case Entity.PARCEL:
                                Vector3 position;
                                HashSet<Parcel> parcels = new HashSet<Parcel>();
                                switch (Vector3.TryParse(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION),
                                            message)),
                                    out position))
                                {
                                    case false:
                                        // Get all sim parcels
                                        ManualResetEvent SimParcelsDownloadedEvent = new ManualResetEvent(false);
                                        EventHandler<SimParcelsDownloadedEventArgs> SimParcelsDownloadedEventHandler =
                                            (sender, args) => SimParcelsDownloadedEvent.Set();
                                        lock (ServicesLock)
                                        {
                                            Client.Parcels.SimParcelsDownloaded += SimParcelsDownloadedEventHandler;
                                            Client.Parcels.RequestAllSimParcels(Client.Network.CurrentSim);
                                            if (Client.Network.CurrentSim.IsParcelMapFull())
                                            {
                                                SimParcelsDownloadedEvent.Set();
                                            }
                                            if (
                                                !SimParcelsDownloadedEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                            {
                                                Client.Parcels.SimParcelsDownloaded -= SimParcelsDownloadedEventHandler;
                                                throw new Exception(
                                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_PARCELS));
                                            }
                                            Client.Parcels.SimParcelsDownloaded -= SimParcelsDownloadedEventHandler;
                                        }
                                        Client.Network.CurrentSim.Parcels.ForEach(o => parcels.Add(o));
                                        break;
                                    case true:
                                        Parcel parcel = null;
                                        if (!GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                                        {
                                            throw new Exception(
                                                wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                                        }
                                        parcels.Add(parcel);
                                        break;
                                }
                                FieldInfo objectReturnTypeField = typeof (ObjectReturnType).GetFields(
                                    BindingFlags.Public |
                                    BindingFlags.Static)
                                    .FirstOrDefault(
                                        o =>
                                            o.Name.Equals(type
                                                .ToLowerInvariant(),
                                                StringComparison.Ordinal));
                                ObjectReturnType returnType = objectReturnTypeField != null
                                    ? (ObjectReturnType)
                                        objectReturnTypeField
                                            .GetValue(null)
                                    : ObjectReturnType.Other;
                                if (!Client.Network.CurrentSim.IsEstateManager)
                                {
                                    Parallel.ForEach(parcels.Where(o => !o.OwnerID.Equals(Client.Self.AgentID)), o =>
                                    {
                                        if (!o.IsGroupOwned || !o.GroupID.Equals(groupUUID))
                                        {
                                            throw new Exception(
                                                wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                        }
                                        GroupPowers power = new GroupPowers();
                                        switch (returnType)
                                        {
                                            case ObjectReturnType.Other:
                                                power = GroupPowers.ReturnNonGroup;
                                                break;
                                            case ObjectReturnType.Group:
                                                power = GroupPowers.ReturnGroupSet;
                                                break;
                                            case ObjectReturnType.Owner:
                                                power = GroupPowers.ReturnGroupOwned;
                                                break;
                                        }
                                        if (!HasGroupPowers(Client.Self.AgentID, groupUUID, power,
                                            Configuration.SERVICES_TIMEOUT))
                                        {
                                            throw new Exception(
                                                wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                        }
                                    });
                                }
                                Parallel.ForEach(parcels,
                                    o =>
                                        Client.Parcels.ReturnObjects(Client.Network.CurrentSim, o.LocalID,
                                            returnType
                                            , new List<UUID> {agentUUID}));

                                break;
                            case Entity.ESTATE:
                                if (!Client.Network.CurrentSim.IsEstateManager)
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_LAND_RIGHTS));
                                }
                                bool allEstates;
                                if (
                                    !bool.TryParse(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ALL),
                                                message)),
                                        out allEstates))
                                {
                                    allEstates = false;
                                }
                                FieldInfo estateReturnFlagsField = typeof (EstateTools.EstateReturnFlags).GetFields(
                                    BindingFlags.Public | BindingFlags.Static)
                                    .FirstOrDefault(
                                        o =>
                                            o.Name.Equals(type,
                                                StringComparison.Ordinal));
                                Client.Estate.SimWideReturn(agentUUID, estateReturnFlagsField != null
                                    ? (EstateTools.EstateReturnFlags)
                                        estateReturnFlagsField
                                            .GetValue(null)
                                    : EstateTools.EstateReturnFlags.ReturnScriptedAndOnOthers, allEstates);
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ENTITY));
                        }
                    };
                    break;
                case ScriptKeys.GETPRIMITIVEOWNERS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        Vector3 position;
                        HashSet<Parcel> parcels = new HashSet<Parcel>();
                        switch (Vector3.TryParse(
                            wasUriUnescapeDataString(wasKeyValueGet(
                                wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                            out position))
                        {
                            case false:
                                // Get all sim parcels
                                ManualResetEvent SimParcelsDownloadedEvent = new ManualResetEvent(false);
                                EventHandler<SimParcelsDownloadedEventArgs> SimParcelsDownloadedEventHandler =
                                    (sender, args) => SimParcelsDownloadedEvent.Set();
                                lock (ServicesLock)
                                {
                                    Client.Parcels.SimParcelsDownloaded += SimParcelsDownloadedEventHandler;
                                    Client.Parcels.RequestAllSimParcels(Client.Network.CurrentSim);
                                    if (Client.Network.CurrentSim.IsParcelMapFull())
                                    {
                                        SimParcelsDownloadedEvent.Set();
                                    }
                                    if (!SimParcelsDownloadedEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                    {
                                        Client.Parcels.SimParcelsDownloaded -= SimParcelsDownloadedEventHandler;
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_PARCELS));
                                    }
                                    Client.Parcels.SimParcelsDownloaded -= SimParcelsDownloadedEventHandler;
                                }
                                Client.Network.CurrentSim.Parcels.ForEach(o => parcels.Add(o));
                                break;
                            case true:
                                Parcel parcel = null;
                                if (!GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                                }
                                parcels.Add(parcel);
                                break;
                        }
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            Parallel.ForEach(parcels.Where(o => !o.OwnerID.Equals(Client.Self.AgentID)), o =>
                            {
                                if (!o.IsGroupOwned || !o.GroupID.Equals(groupUUID))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                }
                                bool permissions = false;
                                Parallel.ForEach(
                                    new HashSet<GroupPowers>
                                    {
                                        GroupPowers.ReturnGroupSet,
                                        GroupPowers.ReturnGroupOwned,
                                        GroupPowers.ReturnNonGroup
                                    }, p =>
                                    {
                                        if (HasGroupPowers(Client.Self.AgentID, groupUUID, p,
                                            Configuration.SERVICES_TIMEOUT))
                                        {
                                            permissions = true;
                                        }
                                    });
                                if (!permissions)
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                }
                            });
                        }
                        ManualResetEvent ParcelObjectOwnersReplyEvent = new ManualResetEvent(false);
                        Dictionary<string, int> primitives = new Dictionary<string, int>();
                        EventHandler<ParcelObjectOwnersReplyEventArgs> ParcelObjectOwnersEventHandler =
                            (sender, args) =>
                            {
                                //object LockObject = new object();
                                foreach (ParcelManager.ParcelPrimOwners primowner in args.PrimOwners)
                                {
                                    string owner = string.Empty;
                                    if (!AgentUUIDToName(primowner.OwnerID, Configuration.SERVICES_TIMEOUT, ref owner))
                                        continue;
                                    if (!primitives.ContainsKey(owner))
                                    {
                                        primitives.Add(owner, primowner.Count);
                                        continue;
                                    }
                                    primitives[owner] += primowner.Count;
                                }
                                ParcelObjectOwnersReplyEvent.Set();
                            };
                        foreach (Parcel parcel in parcels)
                        {
                            lock (ServicesLock)
                            {
                                Client.Parcels.ParcelObjectOwnersReply += ParcelObjectOwnersEventHandler;
                                Client.Parcels.RequestObjectOwners(Client.Network.CurrentSim, parcel.LocalID);
                                if (!ParcelObjectOwnersReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    Client.Parcels.ParcelObjectOwnersReply -= ParcelObjectOwnersEventHandler;
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_LAND_USERS));
                                }
                                Client.Parcels.ParcelObjectOwnersReply -= ParcelObjectOwnersEventHandler;
                            }
                        }
                        if (primitives.Count.Equals(0))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_GET_LAND_USERS));
                        }
                        List<string> data = new List<string>(primitives.Select(
                            p =>
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    new[] {p.Key, p.Value.ToString(CultureInfo.InvariantCulture)})));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()
                                    ));
                        }
                    };
                    break;
                case ScriptKeys.GETGROUPDATA:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        OpenMetaverse.Group dataGroup = new OpenMetaverse.Group();
                        if (!RequestGroup(groupUUID, Configuration.SERVICES_TIMEOUT, ref dataGroup))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        List<string> data = new List<string>(GetStructuredData(dataGroup,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message))));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETPRIMITIVEDATA:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        List<string> data = new List<string>(GetStructuredData(primitive,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message))));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETPARCELDATA:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        Parcel parcel = null;
                        if (
                            !GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                        }
                        List<string> data = new List<string>(GetStructuredData(parcel,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message))));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.SETPARCELDATA:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        Parcel parcel = null;
                        if (
                            !GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                        }
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            if (!parcel.OwnerID.Equals(Client.Self.AgentID))
                            {
                                if (!parcel.IsGroupOwned && !parcel.GroupID.Equals(groupUUID))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                }
                            }
                        }
                        string fields =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message));
                        wasCSVToStructure(fields, ref parcel);
                        parcel.Update(Client.Network.CurrentSim, true);
                    };
                    break;
                case ScriptKeys.GETREGIONPARCELSBOUNDINGBOX:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        // Get all sim parcels
                        ManualResetEvent SimParcelsDownloadedEvent = new ManualResetEvent(false);
                        EventHandler<SimParcelsDownloadedEventArgs> SimParcelsDownloadedEventHandler =
                            (sender, args) => SimParcelsDownloadedEvent.Set();
                        lock (ServicesLock)
                        {
                            Client.Parcels.SimParcelsDownloaded += SimParcelsDownloadedEventHandler;
                            Client.Parcels.RequestAllSimParcels(Client.Network.CurrentSim);
                            if (Client.Network.CurrentSim.IsParcelMapFull())
                            {
                                SimParcelsDownloadedEvent.Set();
                            }
                            if (!SimParcelsDownloadedEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Parcels.SimParcelsDownloaded -= SimParcelsDownloadedEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_PARCELS));
                            }
                            Client.Parcels.SimParcelsDownloaded -= SimParcelsDownloadedEventHandler;
                        }
                        List<Vector3> csv = new List<Vector3>();
                        Client.Network.CurrentSim.Parcels.ForEach(o => csv.AddRange(new[] {o.AABBMin, o.AABBMax}));
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    csv.Select(o => o.ToString()).ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.DOWNLOAD:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string item =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ITEM),
                                message));
                        if (string.IsNullOrEmpty(item))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_ITEM_SPECIFIED));
                        }
                        UUID itemUUID;
                        InventoryItem inventoryItem = null;
                        if (!UUID.TryParse(item, out itemUUID))
                        {
                            InventoryBase inventoryBase =
                                FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, item
                                    ).FirstOrDefault();
                            if (inventoryBase == null)
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                            }
                            inventoryItem = inventoryBase as InventoryItem;
                            if (inventoryItem == null)
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                            }
                            itemUUID = inventoryItem.AssetUUID;
                        }
                        FieldInfo assetTypeInfo = typeof (AssetType).GetFields(BindingFlags.Public |
                                                                               BindingFlags.Static)
                            .FirstOrDefault(
                                o =>
                                    o.Name.Equals(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TYPE), message)),
                                        StringComparison.Ordinal));
                        if (assetTypeInfo == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ASSET_TYPE));
                        }
                        AssetType assetType = (AssetType) assetTypeInfo.GetValue(null);
                        ManualResetEvent RequestAssetEvent = new ManualResetEvent(false);
                        bool succeeded = false;
                        byte[] assetData = null;
                        switch (assetType)
                        {
                            case AssetType.Mesh:
                                Client.Assets.RequestMesh(itemUUID, delegate(bool completed, AssetMesh asset)
                                {
                                    if (!asset.AssetID.Equals(itemUUID)) return;
                                    succeeded = completed;
                                    if (succeeded)
                                    {
                                        assetData = asset.MeshData.AsBinary();
                                    }
                                    RequestAssetEvent.Set();
                                });
                                if (!RequestAssetEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_TRANSFERRING_ASSET));
                                }
                                break;
                                // All of these can only be fetched if they exist locally.
                            case AssetType.LSLText:
                            case AssetType.Notecard:
                                if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INVENTORY))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                                }
                                Client.Assets.RequestInventoryAsset(inventoryItem, true,
                                    delegate(AssetDownload transfer, Asset asset)
                                    {
                                        succeeded = transfer.Success;
                                        if (transfer.Success)
                                        {
                                            assetData = asset.AssetData;
                                        }
                                        RequestAssetEvent.Set();
                                    });
                                if (!RequestAssetEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_TRANSFERRING_ASSET));
                                }
                                break;
                                // All images go through RequestImage and can be fetched directly from the asset server.
                            case AssetType.Texture:
                                Client.Assets.RequestImage(itemUUID, ImageType.Normal,
                                    delegate(TextureRequestState state, AssetTexture asset)
                                    {
                                        if (!asset.AssetID.Equals(itemUUID)) return;
                                        if (!state.Equals(TextureRequestState.Finished)) return;
                                        assetData = asset.AssetData;
                                        succeeded = true;
                                        RequestAssetEvent.Set();
                                    });
                                if (!RequestAssetEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_TRANSFERRING_ASSET));
                                }
                                break;
                                // All of these can be fetched directly from the asset server.
                            case AssetType.Landmark:
                            case AssetType.Gesture:
                            case AssetType.Animation: // Animatn
                            case AssetType.Sound: // Ogg Vorbis
                            case AssetType.Clothing:
                            case AssetType.Bodypart:
                                Client.Assets.RequestAsset(itemUUID, assetType, true,
                                    delegate(AssetDownload transfer, Asset asset)
                                    {
                                        if (!transfer.AssetID.Equals(itemUUID)) return;
                                        succeeded = transfer.Success;
                                        if (transfer.Success)
                                        {
                                            assetData = asset.AssetData;
                                        }
                                        RequestAssetEvent.Set();
                                    });
                                if (!RequestAssetEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_TRANSFERRING_ASSET));
                                }
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ASSET_TYPE));
                        }
                        if (!succeeded)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.FAILED_TO_DOWNLOAD_ASSET));
                        }
                        result.Add(wasGetDescriptionFromEnumValue(ScriptKeys.DATA), Convert.ToBase64String(assetData));
                    };
                    break;
                case ScriptKeys.UPLOAD:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INVENTORY))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string name =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.NAME),
                                message));
                        if (string.IsNullOrEmpty(name))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_NAME_PROVIDED));
                        }
                        uint permissions = 0;
                        Parallel.ForEach(
                            wasUriUnescapeDataString(
                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.PERMISSIONS), message))
                                .Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER}, StringSplitOptions.RemoveEmptyEntries),
                            o =>
                                Parallel.ForEach(
                                    typeof (PermissionMask).GetFields(BindingFlags.Public | BindingFlags.Static)
                                        .Where(p => p.Name.Equals(o, StringComparison.Ordinal)),
                                    q => { permissions |= ((uint) q.GetValue(null)); }));
                        FieldInfo assetTypeInfo = typeof (AssetType).GetFields(BindingFlags.Public |
                                                                               BindingFlags.Static)
                            .FirstOrDefault(o =>
                                o.Name.Equals(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(
                                            wasGetDescriptionFromEnumValue(
                                                ScriptKeys.TYPE),
                                            message)),
                                    StringComparison.Ordinal));
                        if (assetTypeInfo == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ASSET_TYPE));
                        }
                        AssetType assetType = (AssetType) assetTypeInfo.GetValue(null);
                        byte[] data;
                        try
                        {
                            data = Convert.FromBase64String(
                                wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                    message)));
                        }
                        catch (Exception)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_ASSET_DATA));
                        }
                        bool succeeded = false;
                        switch (assetType)
                        {
                            case AssetType.Texture:
                            case AssetType.Sound:
                            case AssetType.Animation:
                                // the holy asset trinity is charged money
                                if (!HasCorradePermission(group, (int) Permissions.PERMISSION_ECONOMY))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                                }
                                if (!UpdateBalance(Configuration.SERVICES_TIMEOUT))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_OBTAIN_MONEY_BALANCE));
                                }
                                if (Client.Self.Balance < Client.Settings.UPLOAD_COST)
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INSUFFICIENT_FUNDS));
                                }
                                // now create and upload the asset
                                ManualResetEvent CreateItemFromAssetEvent = new ManualResetEvent(false);
                                Client.Inventory.RequestCreateItemFromAsset(data, name,
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DESCRIPTION),
                                            message)),
                                    assetType,
                                    (InventoryType)
                                        (typeof (InventoryType).GetFields(BindingFlags.Public | BindingFlags.Static)
                                            .FirstOrDefault(
                                                o => o.Name.Equals(Enum.GetName(typeof (AssetType), assetType),
                                                    StringComparison.Ordinal))).GetValue(null),
                                    Client.Inventory.FindFolderForType(assetType),
                                    delegate(bool completed, string status, UUID itemID, UUID assetID)
                                    {
                                        succeeded = completed;
                                        CreateItemFromAssetEvent.Set();
                                    });
                                if (!CreateItemFromAssetEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_UPLOADING_ASSET));
                                }
                                break;
                            case AssetType.Bodypart:
                            case AssetType.Clothing:
                                FieldInfo wearTypeInfo = typeof (MuteType).GetFields(BindingFlags.Public |
                                                                                     BindingFlags.Static)
                                    .FirstOrDefault(
                                        o =>
                                            o.Name.Equals(
                                                wasUriUnescapeDataString(
                                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.WEAR),
                                                        message)),
                                                StringComparison.Ordinal));
                                if (wearTypeInfo == null)
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_WEARABLE_TYPE));
                                }
                                UUID wearableUUID = Client.Assets.RequestUpload(assetType, data, false);
                                if (wearableUUID.Equals(UUID.Zero))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ASSET_UPLOAD_FAILED));
                                }
                                ManualResetEvent CreateWearableEvent = new ManualResetEvent(false);
                                Client.Inventory.RequestCreateItem(Client.Inventory.FindFolderForType(assetType),
                                    name,
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DESCRIPTION),
                                            message)),
                                    assetType,
                                    wearableUUID, InventoryType.Wearable, (WearableType) wearTypeInfo.GetValue(null),
                                    permissions == 0 ? PermissionMask.Transfer : (PermissionMask) permissions,
                                    delegate(bool completed, InventoryItem createdItem)
                                    {
                                        succeeded = completed;
                                        CreateWearableEvent.Set();
                                    });
                                if (!CreateWearableEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_CREATING_ITEM));
                                }
                                break;
                            case AssetType.Landmark:
                                UUID landmarkUUID = Client.Assets.RequestUpload(assetType, data, false);
                                if (landmarkUUID.Equals(UUID.Zero))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ASSET_UPLOAD_FAILED));
                                }
                                ManualResetEvent CreateLandmarkEvent = new ManualResetEvent(false);
                                Client.Inventory.RequestCreateItem(Client.Inventory.FindFolderForType(assetType),
                                    name,
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DESCRIPTION),
                                            message)),
                                    assetType,
                                    landmarkUUID, InventoryType.Landmark, PermissionMask.All,
                                    delegate(bool completed, InventoryItem createdItem)
                                    {
                                        succeeded = completed;
                                        CreateLandmarkEvent.Set();
                                    });
                                if (!CreateLandmarkEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_CREATING_ITEM));
                                }
                                break;
                            case AssetType.Gesture:
                                ManualResetEvent CreateGestureEvent = new ManualResetEvent(false);
                                InventoryItem newGesture = null;
                                Client.Inventory.RequestCreateItem(Client.Inventory.FindFolderForType(assetType),
                                    name,
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DESCRIPTION),
                                            message)),
                                    assetType,
                                    UUID.Random(), InventoryType.Gesture,
                                    permissions == 0 ? PermissionMask.Transfer : (PermissionMask) permissions,
                                    delegate(bool completed, InventoryItem createdItem)
                                    {
                                        succeeded = completed;
                                        newGesture = createdItem;
                                        CreateGestureEvent.Set();
                                    });
                                if (!CreateGestureEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_CREATING_ITEM));
                                }
                                if (!succeeded)
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_CREATE_ITEM));
                                }
                                ManualResetEvent UploadGestureAssetEvent = new ManualResetEvent(false);
                                Client.Inventory.RequestUploadGestureAsset(data, newGesture.UUID,
                                    delegate(bool completed, string status, UUID itemUUID, UUID assetUUID)
                                    {
                                        succeeded = completed;
                                        UploadGestureAssetEvent.Set();
                                    });
                                if (!UploadGestureAssetEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_UPLOADING_ASSET));
                                }
                                break;
                            case AssetType.Notecard:
                                ManualResetEvent CreateNotecardEvent = new ManualResetEvent(false);
                                InventoryItem newNotecard = null;
                                Client.Inventory.RequestCreateItem(Client.Inventory.FindFolderForType(assetType),
                                    name,
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DESCRIPTION),
                                            message)),
                                    assetType,
                                    UUID.Random(), InventoryType.Notecard,
                                    permissions == 0 ? PermissionMask.Transfer : (PermissionMask) permissions,
                                    delegate(bool completed, InventoryItem createdItem)
                                    {
                                        succeeded = completed;
                                        newNotecard = createdItem;
                                        CreateNotecardEvent.Set();
                                    });
                                if (!CreateNotecardEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_CREATING_ITEM));
                                }
                                if (!succeeded)
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_CREATE_ITEM));
                                }
                                ManualResetEvent UploadNotecardAssetEvent = new ManualResetEvent(false);
                                Client.Inventory.RequestUploadNotecardAsset(data, newNotecard.UUID,
                                    delegate(bool completed, string status, UUID itemUUID, UUID assetUUID)
                                    {
                                        succeeded = completed;
                                        UploadNotecardAssetEvent.Set();
                                    });
                                if (!UploadNotecardAssetEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_UPLOADING_ASSET));
                                }
                                break;
                            case AssetType.LSLText:
                                ManualResetEvent CreateScriptEvent = new ManualResetEvent(false);
                                InventoryItem newScript = null;
                                Client.Inventory.RequestCreateItem(Client.Inventory.FindFolderForType(assetType),
                                    name,
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DESCRIPTION),
                                            message)),
                                    assetType,
                                    UUID.Random(), InventoryType.LSL,
                                    permissions == 0 ? PermissionMask.Transfer : (PermissionMask) permissions,
                                    delegate(bool completed, InventoryItem createdItem)
                                    {
                                        succeeded = completed;
                                        newScript = createdItem;
                                        CreateScriptEvent.Set();
                                    });
                                if (!CreateScriptEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_CREATING_ITEM));
                                }
                                ManualResetEvent UpdateScriptEvent = new ManualResetEvent(false);
                                Client.Inventory.RequestUpdateScriptAgentInventory(data, newScript.UUID, true,
                                    delegate(bool completed, string status, bool compiled, List<string> messages,
                                        UUID itemID, UUID assetID)
                                    {
                                        succeeded = completed;
                                        UpdateScriptEvent.Set();
                                    });
                                if (!UpdateScriptEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_UPLOADING_ASSET));
                                }
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_INVENTORY_TYPE));
                        }
                        if (!succeeded)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ASSET_UPLOAD_FAILED));
                        }
                    };
                    break;
                case ScriptKeys.REZ:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INVENTORY))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        InventoryBase inventoryBaseItem =
                            FindInventory<InventoryBase>(Client.Inventory.Store.RootNode,
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message))
                                ).FirstOrDefault();
                        if (inventoryBaseItem == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_POSITION));
                        }
                        Quaternion rotation;
                        if (
                            !Quaternion.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ROTATION), message)),
                                out rotation))
                        {
                            rotation = Quaternion.CreateFromEulers(0, 0, 0);
                        }
                        Parcel parcel = null;
                        if (!GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                        }
                        if (((uint) parcel.Flags & (uint) ParcelFlags.CreateObjects).Equals(0))
                        {
                            if (!Client.Network.CurrentSim.IsEstateManager)
                            {
                                if (!parcel.OwnerID.Equals(Client.Self.AgentID))
                                {
                                    if (!parcel.IsGroupOwned && !parcel.GroupID.Equals(groupUUID))
                                    {
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                    }
                                    if (!HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.AllowRez,
                                        Configuration.SERVICES_TIMEOUT))
                                    {
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                    }
                                }
                            }
                        }
                        Client.Inventory.RequestRezFromInventory(Client.Network.CurrentSim, rotation, position,
                            inventoryBaseItem as InventoryItem,
                            groupUUID);
                    };
                    break;
                case ScriptKeys.DEREZ:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INVENTORY))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        UUID folderUUID;
                        string folder =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FOLDER),
                                message));
                        if (string.IsNullOrEmpty(folder) || !UUID.TryParse(folder, out folderUUID))
                        {
                            folderUUID =
                                Client.Inventory.Store.Items[Client.Inventory.FindFolderForType(AssetType.Object)].Data
                                    .UUID;
                        }
                        if (folderUUID.Equals(UUID.Zero))
                        {
                            InventoryBase inventoryBaseItem =
                                FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, folder
                                    ).FirstOrDefault();
                            if (inventoryBaseItem != null)
                            {
                                InventoryItem item = inventoryBaseItem as InventoryItem;
                                if (item == null || !item.AssetType.Equals(AssetType.Folder))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.FOLDER_NOT_FOUND));
                                }
                                folderUUID = inventoryBaseItem.UUID;
                            }
                        }
                        FieldInfo deRezDestionationTypeInfo = typeof (DeRezDestination).GetFields(BindingFlags.Public |
                                                                                                  BindingFlags.Static)
                            .FirstOrDefault(
                                o =>
                                    o.Name.Equals(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TYPE), message)),
                                        StringComparison.Ordinal));
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        Client.Inventory.RequestDeRezToInventory(primitive.LocalID, deRezDestionationTypeInfo != null
                            ? (DeRezDestination)
                                deRezDestionationTypeInfo
                                    .GetValue(null)
                            : DeRezDestination.AgentInventoryTake, folderUUID, UUID.Random());
                    };
                    break;
                case ScriptKeys.SETSCRIPTRUNNING:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        string entity =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ENTITY),
                                message));
                        UUID entityUUID;
                        if (!UUID.TryParse(entity, out entityUUID))
                        {
                            if (string.IsNullOrEmpty(entity))
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ENTITY));
                            }
                            entityUUID = UUID.Zero;
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        List<InventoryBase> inventory =
                            Client.Inventory.GetTaskInventory(primitive.ID, primitive.LocalID,
                                Configuration.SERVICES_TIMEOUT).ToList();
                        InventoryItem item = !entityUUID.Equals(UUID.Zero)
                            ? inventory.FirstOrDefault(o => o.UUID.Equals(entityUUID)) as InventoryItem
                            : inventory.FirstOrDefault(o => o.Name.Equals(entity)) as InventoryItem;
                        if (item == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                        }
                        switch (item.AssetType)
                        {
                            case AssetType.LSLBytecode:
                            case AssetType.LSLText:
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ITEM_IS_NOT_A_SCRIPT));
                        }
                        uint action =
                            (uint) wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION), message))
                                    .ToLowerInvariant());
                        switch ((Action) action)
                        {
                            case Action.START:
                            case Action.STOP:
                                Client.Inventory.RequestSetScriptRunning(primitive.ID, item.UUID,
                                    action.Equals((uint) Action.START));
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ACTION));
                        }
                        ManualResetEvent ScriptRunningReplyEvent = new ManualResetEvent(false);
                        bool succeeded = false;
                        EventHandler<ScriptRunningReplyEventArgs> ScriptRunningEventHandler = (sender, args) =>
                        {
                            switch ((Action) action)
                            {
                                case Action.START:
                                    succeeded = args.IsRunning;
                                    break;
                                case Action.STOP:
                                    succeeded = !args.IsRunning;
                                    break;
                            }
                            ScriptRunningReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Inventory.ScriptRunningReply += ScriptRunningEventHandler;
                            Client.Inventory.RequestGetScriptRunning(primitive.ID, item.UUID);
                            if (!ScriptRunningReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Inventory.ScriptRunningReply -= ScriptRunningEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_SCRIPT_STATE));
                            }
                            Client.Inventory.ScriptRunningReply -= ScriptRunningEventHandler;
                        }
                        if (!succeeded)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_SET_SCRIPT_STATE));
                        }
                    };
                    break;
                case ScriptKeys.GETSCRIPTRUNNING:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        string entity =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ENTITY),
                                message));
                        UUID entityUUID;
                        if (!UUID.TryParse(entity, out entityUUID))
                        {
                            if (string.IsNullOrEmpty(entity))
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ENTITY));
                            }
                            entityUUID = UUID.Zero;
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        List<InventoryBase> inventory =
                            Client.Inventory.GetTaskInventory(primitive.ID, primitive.LocalID,
                                Configuration.SERVICES_TIMEOUT).ToList();
                        InventoryItem item = !entityUUID.Equals(UUID.Zero)
                            ? inventory.FirstOrDefault(o => o.UUID.Equals(entityUUID)) as InventoryItem
                            : inventory.FirstOrDefault(o => o.Name.Equals(entity)) as InventoryItem;
                        if (item == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                        }
                        switch (item.AssetType)
                        {
                            case AssetType.LSLBytecode:
                            case AssetType.LSLText:
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.ITEM_IS_NOT_A_SCRIPT));
                        }
                        ManualResetEvent ScriptRunningReplyEvent = new ManualResetEvent(false);
                        bool running = false;
                        EventHandler<ScriptRunningReplyEventArgs> ScriptRunningEventHandler = (sender, args) =>
                        {
                            running = args.IsRunning;
                            ScriptRunningReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Inventory.ScriptRunningReply += ScriptRunningEventHandler;
                            Client.Inventory.RequestGetScriptRunning(primitive.ID, item.UUID);
                            if (!ScriptRunningReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Inventory.ScriptRunningReply -= ScriptRunningEventHandler;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_SCRIPT_STATE));
                            }
                            Client.Inventory.ScriptRunningReply -= ScriptRunningEventHandler;
                        }
                        result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA), running.ToString());
                    };
                    break;
                case ScriptKeys.GETPRIMITIVEINVENTORY:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        List<string> data =
                            new List<string>(Client.Inventory.GetTaskInventory(primitive.ID, primitive.LocalID,
                                Configuration.SERVICES_TIMEOUT).Select(o => new[]
                                {
                                    o.Name,
                                    o.UUID.ToString()
                                }).SelectMany(o => o));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETPRIMITIVEINVENTORYDATA:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        string entity =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ENTITY),
                                message));
                        UUID entityUUID;
                        if (!UUID.TryParse(entity, out entityUUID))
                        {
                            if (string.IsNullOrEmpty(entity))
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ENTITY));
                            }
                            entityUUID = UUID.Zero;
                        }
                        List<InventoryBase> inventory =
                            Client.Inventory.GetTaskInventory(primitive.ID, primitive.LocalID,
                                Configuration.SERVICES_TIMEOUT).ToList();
                        InventoryItem item = !entityUUID.Equals(UUID.Zero)
                            ? inventory.FirstOrDefault(o => o.UUID.Equals(entityUUID)) as InventoryItem
                            : inventory.FirstOrDefault(o => o.Name.Equals(entity)) as InventoryItem;
                        if (item == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                        }
                        List<string> data = new List<string>(GetStructuredData(item,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message))));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.UPDATEPRIMITIVEINVENTORY:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        string entity =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ENTITY),
                                message));
                        UUID entityUUID;
                        if (!UUID.TryParse(entity, out entityUUID))
                        {
                            if (string.IsNullOrEmpty(entity))
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ENTITY));
                            }
                            entityUUID = UUID.Zero;
                        }
                        switch (
                            wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        message)).ToLowerInvariant()))
                        {
                            case Action.ADD:
                                InventoryBase inventoryBaseItem =
                                    FindInventory<InventoryBase>(Client.Inventory.Store.RootNode,
                                        !entityUUID.Equals(UUID.Zero) ? entityUUID.ToString() : entity
                                        ).FirstOrDefault();
                                if (inventoryBaseItem == null)
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                                }
                                Client.Inventory.UpdateTaskInventory(primitive.LocalID,
                                    inventoryBaseItem as InventoryItem);
                                break;
                            case Action.REMOVE:
                                if (entityUUID.Equals(UUID.Zero))
                                {
                                    entityUUID = Client.Inventory.GetTaskInventory(primitive.ID, primitive.LocalID,
                                        Configuration.SERVICES_TIMEOUT).FirstOrDefault(o => o.Name.Equals(entity)).UUID;
                                    if (entityUUID.Equals(UUID.Zero))
                                    {
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                                    }
                                }
                                Client.Inventory.RemoveTaskInventory(primitive.LocalID, entityUUID,
                                    Client.Network.CurrentSim);
                                break;
                            case Action.TAKE:
                                InventoryBase inventoryBase = !entityUUID.Equals(UUID.Zero)
                                    ? Client.Inventory.GetTaskInventory(primitive.ID, primitive.LocalID,
                                        Configuration.SERVICES_TIMEOUT).FirstOrDefault(o => o.UUID.Equals(entityUUID))
                                    : Client.Inventory.GetTaskInventory(primitive.ID, primitive.LocalID,
                                        Configuration.SERVICES_TIMEOUT).FirstOrDefault(o => o.Name.Equals(entity));
                                InventoryItem inventoryItem = inventoryBase as InventoryItem;
                                if (inventoryItem == null)
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                                }
                                UUID folderUUID;
                                string folder =
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FOLDER),
                                            message));
                                if (string.IsNullOrEmpty(folder) || !UUID.TryParse(folder, out folderUUID))
                                {
                                    folderUUID =
                                        Client.Inventory.Store.Items[
                                            Client.Inventory.FindFolderForType(inventoryItem.AssetType)].Data
                                            .UUID;
                                }
                                Client.Inventory.MoveTaskInventory(primitive.LocalID, inventoryItem.UUID, folderUUID,
                                    Client.Network.CurrentSim);
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.GETINVENTORYDATA:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INVENTORY))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        InventoryBase inventoryBaseItem =
                            FindInventory<InventoryBase>(Client.Inventory.Store.RootNode,
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message))
                                ).FirstOrDefault();
                        if (inventoryBaseItem == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                        }
                        List<string> data = new List<string>(GetStructuredData(inventoryBaseItem as InventoryItem,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message))));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.SEARCHINVENTORY:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INVENTORY))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        HashSet<AssetType> assetTypes = new HashSet<AssetType>();
                        Parallel.ForEach(
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TYPE),
                                message))
                                .Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER}, StringSplitOptions.RemoveEmptyEntries),
                            o => Parallel.ForEach(
                                typeof (AssetType).GetFields(BindingFlags.Public | BindingFlags.Static)
                                    .Where(p => p.Name.Equals(o, StringComparison.Ordinal)),
                                q => assetTypes.Add((AssetType) q.GetValue(null))));
                        string pattern =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.PATTERN),
                                message));
                        if (string.IsNullOrEmpty(pattern))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_PATTERN_PROVIDED));
                        }
                        Regex search;
                        try
                        {
                            search = new Regex(pattern, RegexOptions.Compiled);
                        }
                        catch
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_COMPILE_REGULAR_EXPRESSION));
                        }
                        List<string> csv = new List<string>();
                        object LockObject = new object();
                        Parallel.ForEach(FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, search
                            ),
                            o =>
                            {
                                InventoryItem inventoryItem = o as InventoryItem;
                                if (inventoryItem == null) return;
                                if (!assetTypes.Count.Equals(0) && !assetTypes.Contains(inventoryItem.AssetType))
                                    return;
                                lock (LockObject)
                                {
                                    csv.Add(Enum.GetName(typeof (AssetType), inventoryItem.AssetType));
                                    csv.Add(inventoryItem.Name);
                                    csv.Add(inventoryItem.AssetUUID.ToString());
                                }
                            });
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETINVENTORYPATH:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INVENTORY))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        HashSet<AssetType> assetTypes = new HashSet<AssetType>();
                        Parallel.ForEach(
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TYPE),
                                message))
                                .Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER}, StringSplitOptions.RemoveEmptyEntries),
                            o => Parallel.ForEach(
                                typeof (AssetType).GetFields(BindingFlags.Public | BindingFlags.Static)
                                    .Where(p => p.Name.Equals(o, StringComparison.Ordinal)),
                                q => assetTypes.Add((AssetType) q.GetValue(null))));
                        string pattern =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.PATTERN),
                                message));
                        if (string.IsNullOrEmpty(pattern))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_PATTERN_PROVIDED));
                        }
                        Regex search;
                        try
                        {
                            search = new Regex(pattern, RegexOptions.Compiled);
                        }
                        catch
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_COMPILE_REGULAR_EXPRESSION));
                        }
                        List<string> csv = new List<string>();
                        Parallel.ForEach(FindInventoryPath<InventoryBase>(Client.Inventory.Store.RootNode,
                            search, new LinkedList<string>()).Select(o => o.Value),
                            o => csv.Add(string.Join(CORRADE_CONSTANTS.PATH_SEPARATOR, o.ToArray())));
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETPARTICLESYSTEM:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        StringBuilder particleSystem = new StringBuilder();
                        particleSystem.Append("PSYS_PART_FLAGS, 0");
                        if (!((long) primitive.ParticleSys.PartDataFlags &
                              (long) Primitive.ParticleSystem.ParticleDataFlags.InterpColor).Equals(0))
                            particleSystem.Append(" | PSYS_PART_INTERP_COLOR_MASK");
                        if (!((long) primitive.ParticleSys.PartDataFlags &
                              (long) Primitive.ParticleSystem.ParticleDataFlags.InterpScale).Equals(0))
                            particleSystem.Append(" | PSYS_PART_INTERP_SCALE_MASK");
                        if (
                            !((long) primitive.ParticleSys.PartDataFlags &
                              (long) Primitive.ParticleSystem.ParticleDataFlags.Bounce).Equals(0))
                            particleSystem.Append(" | PSYS_PART_BOUNCE_MASK");
                        if (
                            !((long) primitive.ParticleSys.PartDataFlags &
                              (long) Primitive.ParticleSystem.ParticleDataFlags.Wind).Equals(0))
                            particleSystem.Append(" | PSYS_PART_WIND_MASK");
                        if (
                            !((long) primitive.ParticleSys.PartDataFlags &
                              (long) Primitive.ParticleSystem.ParticleDataFlags.FollowSrc).Equals(0))
                            particleSystem.Append(" | PSYS_PART_FOLLOW_SRC_MASK");
                        if (!((long) primitive.ParticleSys.PartDataFlags &
                              (long) Primitive.ParticleSystem.ParticleDataFlags.FollowVelocity).Equals(0))
                            particleSystem.Append(" | PSYS_PART_FOLLOW_VELOCITY_MASK");
                        if (
                            !((long) primitive.ParticleSys.PartDataFlags &
                              (long) Primitive.ParticleSystem.ParticleDataFlags.TargetPos).Equals(0))
                            particleSystem.Append(" | PSYS_PART_TARGET_POS_MASK");
                        if (!((long) primitive.ParticleSys.PartDataFlags &
                              (long) Primitive.ParticleSystem.ParticleDataFlags.TargetLinear).Equals(0))
                            particleSystem.Append(" | PSYS_PART_TARGET_LINEAR_MASK");
                        if (
                            !((long) primitive.ParticleSys.PartDataFlags &
                              (long) Primitive.ParticleSystem.ParticleDataFlags.Emissive).Equals(0))
                            particleSystem.Append(" | PSYS_PART_EMISSIVE_MASK");
                        particleSystem.Append(LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_PATTERN, 0");
                        if (
                            !((long) primitive.ParticleSys.Pattern & (long) Primitive.ParticleSystem.SourcePattern.Drop)
                                .Equals(0))
                            particleSystem.Append(" | PSYS_SRC_PATTERN_DROP");
                        if (!((long) primitive.ParticleSys.Pattern &
                              (long) Primitive.ParticleSystem.SourcePattern.Explode).Equals(0))
                            particleSystem.Append(" | PSYS_SRC_PATTERN_EXPLODE");
                        if (
                            !((long) primitive.ParticleSys.Pattern & (long) Primitive.ParticleSystem.SourcePattern.Angle)
                                .Equals(0))
                            particleSystem.Append(" | PSYS_SRC_PATTERN_ANGLE");
                        if (!((long) primitive.ParticleSys.Pattern &
                              (long) Primitive.ParticleSystem.SourcePattern.AngleCone).Equals(0))
                            particleSystem.Append(" | PSYS_SRC_PATTERN_ANGLE_CONE");
                        if (!((long) primitive.ParticleSys.Pattern &
                              (long) Primitive.ParticleSystem.SourcePattern.AngleConeEmpty).Equals(0))
                            particleSystem.Append(" | PSYS_SRC_PATTERN_ANGLE_CONE_EMPTY");
                        particleSystem.Append(LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_PART_START_ALPHA, " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.PartStartColor.A) +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_PART_END_ALPHA, " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.PartEndColor.A) +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_PART_START_COLOR, " +
                                              primitive.ParticleSys.PartStartColor.ToRGBString() +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_PART_END_COLOR, " + primitive.ParticleSys.PartEndColor.ToRGBString() +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_PART_START_SCALE, <" +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.PartStartScaleX) + ", " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.PartStartScaleY) +
                                              ", 0>, ");
                        particleSystem.Append("PSYS_PART_END_SCALE, <" +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.PartEndScaleX) + ", " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.PartEndScaleY) +
                                              ", 0>, ");
                        particleSystem.Append("PSYS_PART_MAX_AGE, " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.PartMaxAge) +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_MAX_AGE, " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.MaxAge) +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_ACCEL, " + primitive.ParticleSys.PartAcceleration +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_BURST_PART_COUNT, " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0}",
                                                  primitive.ParticleSys.BurstPartCount) +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_BURST_RADIUS, " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.BurstRadius) +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_BURST_RATE, " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.BurstRate) +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_BURST_SPEED_MIN, " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.BurstSpeedMin) +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_BURST_SPEED_MAX, " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.BurstSpeedMax) +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_INNERANGLE, " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.InnerAngle) +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_OUTERANGLE, " +
                                              string.Format(CultureInfo.InvariantCulture, "{0:0.00000}",
                                                  primitive.ParticleSys.OuterAngle) +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_OMEGA, " + primitive.ParticleSys.AngularVelocity +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_TEXTURE, (key)\"" + primitive.ParticleSys.Texture + "\"" +
                                              LINDEN_CONSTANTS.LSL.CSV_DELIMITER);
                        particleSystem.Append("PSYS_SRC_TARGET_KEY, (key)\"" + primitive.ParticleSys.Target + "\"");
                        result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA), particleSystem.ToString());
                    };
                    break;
                case ScriptKeys.CREATENOTECARD:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INVENTORY))
                        {
                            throw new Exception(
                                wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string name =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.NAME),
                                message));
                        if (string.IsNullOrEmpty(name))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_NAME_PROVIDED));
                        }
                        ManualResetEvent CreateNotecardEvent = new ManualResetEvent(false);
                        bool succeeded = false;
                        InventoryItem newItem = null;
                        Client.Inventory.RequestCreateItem(Client.Inventory.FindFolderForType(AssetType.Notecard),
                            name,
                            wasUriUnescapeDataString(
                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DESCRIPTION), message)),
                            AssetType.Notecard,
                            UUID.Random(), InventoryType.Notecard, PermissionMask.All,
                            delegate(bool completed, InventoryItem createdItem)
                            {
                                succeeded = completed;
                                newItem = createdItem;
                                CreateNotecardEvent.Set();
                            });
                        if (!CreateNotecardEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_CREATING_ITEM));
                        }
                        if (!succeeded)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_CREATE_ITEM));
                        }
                        AssetNotecard blank = new AssetNotecard
                        {
                            BodyText = LINDEN_CONSTANTS.ASSETS.NOTECARD.NEWLINE
                        };
                        blank.Encode();
                        ManualResetEvent UploadBlankNotecardEvent = new ManualResetEvent(false);
                        succeeded = false;
                        Client.Inventory.RequestUploadNotecardAsset(blank.AssetData, newItem.UUID,
                            delegate(bool completed, string status, UUID itemUUID, UUID assetUUID)
                            {
                                succeeded = completed;
                                UploadBlankNotecardEvent.Set();
                            });
                        if (!UploadBlankNotecardEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_UPLOADING_ITEM));
                        }
                        if (!succeeded)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_UPLOAD_ITEM));
                        }
                        string text =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TEXT),
                                message));
                        if (!string.IsNullOrEmpty(text))
                        {
                            AssetNotecard notecard = new AssetNotecard
                            {
                                BodyText = text
                            };
                            notecard.Encode();
                            ManualResetEvent UploadNotecardDataEvent = new ManualResetEvent(false);
                            succeeded = false;
                            Client.Inventory.RequestUploadNotecardAsset(notecard.AssetData, newItem.UUID,
                                delegate(bool completed, string status, UUID itemUUID, UUID assetUUID)
                                {
                                    succeeded = completed;
                                    UploadNotecardDataEvent.Set();
                                });
                            if (!UploadNotecardDataEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_UPLOADING_ITEM_DATA));
                            }
                            if (!succeeded)
                            {
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.UNABLE_TO_UPLOAD_ITEM_DATA));
                            }
                        }
                    };
                    break;
                case ScriptKeys.ACTIVATE:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_IN_GROUP));
                        }
                        Client.Groups.ActivateGroup(groupUUID);
                    };
                    break;
                case ScriptKeys.SETTITLE:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_IN_GROUP));
                        }
                        ManualResetEvent GroupRoleDataReplyEvent = new ManualResetEvent(false);
                        Dictionary<string, UUID> roleData = new Dictionary<string, UUID>();
                        EventHandler<GroupRolesDataReplyEventArgs> Groups_GroupRoleDataReply = (sender, args) =>
                        {
                            roleData = args.Roles.ToDictionary(o => o.Value.Title, o => o.Value.ID);
                            GroupRoleDataReplyEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Groups.GroupRoleDataReply += Groups_GroupRoleDataReply;
                            Client.Groups.RequestGroupRoles(groupUUID);
                            if (!GroupRoleDataReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Groups.GroupRoleDataReply -= Groups_GroupRoleDataReply;
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_GROUP_ROLES));
                            }
                            Client.Groups.GroupRoleDataReply -= Groups_GroupRoleDataReply;
                        }
                        UUID roleUUID =
                            roleData.FirstOrDefault(
                                o =>
                                    o.Key.Equals(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TITLE),
                                                message)),
                                        StringComparison.Ordinal))
                                .Value;
                        if (roleUUID.Equals(UUID.Zero))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_TITLE));
                        }
                        Client.Groups.ActivateTitle(groupUUID, roleUUID);
                    };
                    break;
                case ScriptKeys.MOVE:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        switch (
                            wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        message)).ToLowerInvariant()))
                        {
                            case Action.START:
                                Vector3 position;
                                if (
                                    !Vector3.TryParse(
                                        wasUriUnescapeDataString(wasKeyValueGet(
                                            wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                        out position))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_POSITION));
                                }
                                uint moveRegionX, moveRegionY;
                                Utils.LongToUInts(Client.Network.CurrentSim.Handle, out moveRegionX, out moveRegionY);
                                if (Client.Self.Movement.SitOnGround || !Client.Self.SittingOn.Equals(0))
                                {
                                    Client.Self.Stand();
                                }
                                Client.Self.SignaledAnimations.ForEach(
                                    animation => Client.Self.AnimationStop(animation.Key, true));
                                Client.Self.AutoPilotCancel();
                                Client.Self.Movement.TurnToward(position, true);
                                Client.Self.AutoPilot(position.X + moveRegionX, position.Y + moveRegionY, position.Z);
                                break;
                            case Action.STOP:
                                Client.Self.AutoPilotCancel();
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_MOVE_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.TURNTO:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_POSITION));
                        }
                        Client.Self.Movement.TurnToward(position, true);
                    };
                    break;
                case ScriptKeys.NUDGE:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        switch (wasGetEnumValueFromDescription<Direction>(
                            wasUriUnescapeDataString(wasKeyValueGet(
                                wasGetDescriptionFromEnumValue(ScriptKeys.DIRECTION),
                                message))
                                .ToLowerInvariant()))
                        {
                            case Direction.BACK:
                                Client.Self.Movement.SendManualUpdate(AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG,
                                    Client.Self.Movement.Camera.Position,
                                    Client.Self.Movement.Camera.AtAxis, Client.Self.Movement.Camera.LeftAxis,
                                    Client.Self.Movement.Camera.UpAxis,
                                    Client.Self.Movement.BodyRotation, Client.Self.Movement.HeadRotation,
                                    Client.Self.Movement.Camera.Far, AgentFlags.None, AgentState.None, true);
                                break;
                            case Direction.FORWARD:
                                Client.Self.Movement.SendManualUpdate(AgentManager.ControlFlags.AGENT_CONTROL_AT_POS,
                                    Client.Self.Movement.Camera.Position,
                                    Client.Self.Movement.Camera.AtAxis, Client.Self.Movement.Camera.LeftAxis,
                                    Client.Self.Movement.Camera.UpAxis,
                                    Client.Self.Movement.BodyRotation, Client.Self.Movement.HeadRotation,
                                    Client.Self.Movement.Camera.Far, AgentFlags.None,
                                    AgentState.None, true);
                                break;
                            case Direction.LEFT:
                                Client.Self.Movement.SendManualUpdate(AgentManager.ControlFlags.
                                    AGENT_CONTROL_LEFT_POS, Client.Self.Movement.Camera.Position,
                                    Client.Self.Movement.Camera.AtAxis, Client.Self.Movement.Camera.LeftAxis,
                                    Client.Self.Movement.Camera.UpAxis,
                                    Client.Self.Movement.BodyRotation, Client.Self.Movement.HeadRotation,
                                    Client.Self.Movement.Camera.Far, AgentFlags.None,
                                    AgentState.None, true);
                                break;
                            case Direction.RIGHT:
                                Client.Self.Movement.SendManualUpdate(AgentManager.ControlFlags.
                                    AGENT_CONTROL_LEFT_NEG, Client.Self.Movement.Camera.Position,
                                    Client.Self.Movement.Camera.AtAxis, Client.Self.Movement.Camera.LeftAxis,
                                    Client.Self.Movement.Camera.UpAxis,
                                    Client.Self.Movement.BodyRotation, Client.Self.Movement.HeadRotation,
                                    Client.Self.Movement.Camera.Far, AgentFlags.None,
                                    AgentState.None, true);
                                break;
                            case Direction.UP:
                                Client.Self.Movement.SendManualUpdate(AgentManager.ControlFlags.AGENT_CONTROL_UP_POS,
                                    Client.Self.Movement.Camera.Position,
                                    Client.Self.Movement.Camera.AtAxis, Client.Self.Movement.Camera.LeftAxis,
                                    Client.Self.Movement.Camera.UpAxis,
                                    Client.Self.Movement.BodyRotation, Client.Self.Movement.HeadRotation,
                                    Client.Self.Movement.Camera.Far, AgentFlags.None,
                                    AgentState.None, true);
                                break;
                            case Direction.DOWN:
                                Client.Self.Movement.SendManualUpdate(AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG,
                                    Client.Self.Movement.Camera.Position,
                                    Client.Self.Movement.Camera.AtAxis, Client.Self.Movement.Camera.LeftAxis,
                                    Client.Self.Movement.Camera.UpAxis,
                                    Client.Self.Movement.BodyRotation, Client.Self.Movement.HeadRotation,
                                    Client.Self.Movement.Camera.Far, AgentFlags.None,
                                    AgentState.None, true);
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_DIRECTION));
                        }
                    };
                    break;
                case ScriptKeys.STARTPROPOSAL:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROUP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        if (!AgentInGroup(Client.Self.AgentID, groupUUID, Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NOT_IN_GROUP));
                        }
                        if (
                            !HasGroupPowers(Client.Self.AgentID, groupUUID, GroupPowers.StartProposal,
                                Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                        }
                        int duration;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DURATION), message)),
                                out duration))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_PROPOSAL_DURATION));
                        }
                        float majority;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.MAJORITY), message)),
                                out majority))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_PROPOSAL_MAJORITY));
                        }
                        int quorum;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.QUORUM), message)),
                                out quorum))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_PROPOSAL_QUORUM));
                        }
                        string text =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TEXT),
                                message));
                        if (string.IsNullOrEmpty(text))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_PROPOSAL_TEXT));
                        }
                        Client.Groups.StartProposal(groupUUID, new GroupProposal
                        {
                            Duration = duration,
                            Majority = majority,
                            Quorum = quorum,
                            VoteText = text
                        });
                    };
                    break;
                case ScriptKeys.MUTE:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group, (int) Permissions.PERMISSION_MUTE))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID targetUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TARGET), message)),
                                out targetUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_MUTE_TARGET));
                        }
                        switch (
                            wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        message)).ToLowerInvariant()))
                        {
                            case Action.MUTE:
                                FieldInfo muteTypeInfo = typeof (MuteType).GetFields(BindingFlags.Public |
                                                                                     BindingFlags.Static)
                                    .FirstOrDefault(
                                        o =>
                                            o.Name.Equals(
                                                wasUriUnescapeDataString(
                                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TYPE),
                                                        message)),
                                                StringComparison.Ordinal));
                                ManualResetEvent MuteListUpdatedEvent = new ManualResetEvent(false);
                                EventHandler<EventArgs> MuteListUpdatedEventHandler =
                                    (sender, args) => MuteListUpdatedEvent.Set();
                                lock (ServicesLock)
                                {
                                    Client.Self.MuteListUpdated += MuteListUpdatedEventHandler;
                                    Client.Self.UpdateMuteListEntry(muteTypeInfo != null
                                        ? (MuteType)
                                            muteTypeInfo
                                                .GetValue(null)
                                        : MuteType.ByName, targetUUID,
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.NAME),
                                                message)));
                                    if (!MuteListUpdatedEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                    {
                                        Client.Self.MuteListUpdated -= MuteListUpdatedEventHandler;
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_UPDATING_MUTE_LIST));
                                    }
                                    Client.Self.MuteListUpdated -= MuteListUpdatedEventHandler;
                                }
                                break;
                            case Action.UNMUTE:
                                Client.Self.RemoveMuteListEntry(targetUUID,
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.NAME), message)));
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.GETMUTES:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_MUTE))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        List<string> data = new List<string>(Client.Self.MuteList.Copy().Select(o => new[]
                        {
                            o.Value.Name,
                            o.Value.ID.ToString()
                        }).SelectMany(o => o));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.DATABASE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_DATABASE))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string databaseFile =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).DatabaseFile;
                        if (string.IsNullOrEmpty(databaseFile))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_DATABASE_FILE_CONFIGURED));
                        }
                        if (!File.Exists(databaseFile))
                        {
                            // create the file and close it
                            File.Create(databaseFile).Close();
                        }
                        switch (
                            wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        message)).ToLowerInvariant()))
                        {
                            case Action.GET:
                                string databaseGetkey =
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.KEY), message));
                                if (string.IsNullOrEmpty(databaseGetkey))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_DATABASE_KEY_SPECIFIED));
                                }
                                lock (DatabaseFileLock)
                                {
                                    if (!DatabaseLocks.ContainsKey(group))
                                    {
                                        DatabaseLocks.Add(group, new object());
                                    }
                                }
                                lock (DatabaseLocks[group])
                                {
                                    string databaseGetValue = wasKeyValueGet(databaseGetkey,
                                        File.ReadAllText(databaseFile));
                                    if (!string.IsNullOrEmpty(databaseGetValue))
                                    {
                                        result.Add(databaseGetkey,
                                            wasUriUnescapeDataString(databaseGetValue));
                                    }
                                }
                                lock (DatabaseFileLock)
                                {
                                    if (DatabaseLocks.ContainsKey(group))
                                    {
                                        DatabaseLocks.Remove(group);
                                    }
                                }
                                break;
                            case Action.SET:
                                string databaseSetKey =
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.KEY), message));
                                if (string.IsNullOrEmpty(databaseSetKey))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_DATABASE_KEY_SPECIFIED));
                                }
                                string databaseSetValue =
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.VALUE),
                                            message));
                                if (string.IsNullOrEmpty(databaseSetValue))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_DATABASE_VALUE_SPECIFIED));
                                }
                                lock (DatabaseFileLock)
                                {
                                    if (!DatabaseLocks.ContainsKey(group))
                                    {
                                        DatabaseLocks.Add(group, new object());
                                    }
                                }
                                lock (DatabaseLocks[group])
                                {
                                    string contents = File.ReadAllText(databaseFile);
                                    using (StreamWriter recreateDatabase = new StreamWriter(databaseFile, false))
                                    {
                                        recreateDatabase.Write(wasKeyValueSet(databaseSetKey,
                                            databaseSetValue, contents));
                                        recreateDatabase.Flush();
                                        //recreateDatabase.Close();
                                    }
                                }
                                lock (DatabaseFileLock)
                                {
                                    if (DatabaseLocks.ContainsKey(group))
                                    {
                                        DatabaseLocks.Remove(group);
                                    }
                                }
                                break;
                            case Action.DELETE:
                                string databaseDeleteKey =
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.KEY), message));
                                if (string.IsNullOrEmpty(databaseDeleteKey))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_DATABASE_KEY_SPECIFIED));
                                }
                                lock (DatabaseFileLock)
                                {
                                    if (!DatabaseLocks.ContainsKey(group))
                                    {
                                        DatabaseLocks.Add(group, new object());
                                    }
                                }
                                lock (DatabaseLocks[group])
                                {
                                    string contents = File.ReadAllText(databaseFile);
                                    using (StreamWriter recreateDatabase = new StreamWriter(databaseFile, false))
                                    {
                                        recreateDatabase.Write(wasKeyValueDelete(databaseDeleteKey, contents));
                                        recreateDatabase.Flush();
                                        //recreateDatabase.Close();
                                    }
                                }
                                lock (DatabaseFileLock)
                                {
                                    if (DatabaseLocks.ContainsKey(group))
                                    {
                                        DatabaseLocks.Remove(group);
                                    }
                                }
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_DATABASE_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.NOTIFY:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_NOTIFICATIONS))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        switch (
                            wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        message)).ToLowerInvariant()))
                        {
                            case Action.SET:
                                string url =
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.URL), message));
                                if (string.IsNullOrEmpty(url))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_URL_PROVIDED));
                                }
                                Uri notifyURL;
                                if (!Uri.TryCreate(url, UriKind.Absolute, out notifyURL))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_URL_PROVIDED));
                                }
                                string notificationTypes =
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TYPE), message))
                                        .ToLowerInvariant();
                                if (string.IsNullOrEmpty(notificationTypes))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.INVALID_NOTIFICATION_TYPES));
                                }
                                uint notifications = 0;
                                Parallel.ForEach(
                                    notificationTypes.Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER},
                                        StringSplitOptions.RemoveEmptyEntries),
                                    o =>
                                    {
                                        uint notificationValue = (uint) wasGetEnumValueFromDescription<Notifications>(o);
                                        if (!HasCorradeNotification(group, notificationValue))
                                        {
                                            throw new Exception(
                                                wasGetDescriptionFromEnumValue(ScriptError.NOTIFICATION_NOT_ALLOWED));
                                        }
                                        notifications |= notificationValue;
                                    });
                                // Build the notification.
                                Notification notification = new Notification
                                {
                                    GROUP = group,
                                    URL = url,
                                    NOTIFICATION_MASK = notifications
                                };
                                lock (GroupNotificationsLock)
                                {
                                    // Replace notification.
                                    GroupNotifications.RemoveWhere(
                                        o => o.GROUP.Equals(group, StringComparison.Ordinal));
                                    GroupNotifications.Add(notification);
                                }
                                break;
                            case Action.GET:
                                // If the group has no insalled notifications, bail
                                lock (GroupNotificationsLock)
                                {
                                    if (!GroupNotifications.Any(o => o.GROUP.Equals(group)))
                                    {
                                        break;
                                    }
                                }
                                List<string> data;
                                lock (GroupNotificationsLock)
                                {
                                    data =
                                        new List<string>(
                                            wasGetEnumDescriptions<Notifications>().Where(o => !GroupNotifications.Any(
                                                p =>
                                                    p.GROUP.Equals(group, StringComparison.Ordinal) &&
                                                    (p.NOTIFICATION_MASK &
                                                     (uint) wasGetEnumValueFromDescription<Notifications>(o)).Equals(0))));
                                }
                                if (!data.Count.Equals(0))
                                {
                                    result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                        string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                            data.ToArray()));
                                }
                                break;
                            default:
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_NOTIFICATIONS_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.REPLYTOTELEPORTLURE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        UUID sessionUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.SESSION), message)),
                                out sessionUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_SESSION_SPECIFIED));
                        }
                        Client.Self.TeleportLureRespond(agentUUID, sessionUUID, wasGetEnumValueFromDescription<Action>(
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                message))
                                .ToLowerInvariant()).Equals(Action.ACCEPT));
                    };
                    break;
                case ScriptKeys.GETTELEPORTLURES:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        List<string> csv = new List<string>();
                        object LockObject = new object();
                        lock (TeleportLureLock)
                        {
                            Parallel.ForEach(TeleportLures, o =>
                            {
                                lock (LockObject)
                                {
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o.Agent, o.Agent.FirstName), o.Agent.FirstName});
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o.Agent, o.Agent.LastName), o.Agent.LastName});
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o.Agent, o.Agent.UUID), o.Agent.UUID.ToString()});
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o, o.Session), o.Session.ToString()});
                                }
                            });
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.REPLYTOSCRIPTPERMISSIONREQUEST:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID itemUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                out itemUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_ITEM_SPECIFIED));
                        }
                        UUID taskUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.TASK), message)),
                                out taskUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_TASK_SPECIFIED));
                        }
                        int permissionMask = 0;
                        Parallel.ForEach(
                            wasUriUnescapeDataString(
                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.PERMISSIONS), message))
                                .Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER}, StringSplitOptions.RemoveEmptyEntries),
                            o =>
                                Parallel.ForEach(
                                    typeof (ScriptPermission).GetFields(BindingFlags.Public | BindingFlags.Static)
                                        .Where(p => p.Name.Equals(o, StringComparison.Ordinal)),
                                    q => { permissionMask |= ((int) q.GetValue(null)); }));
                        Client.Self.ScriptQuestionReply(Client.Network.CurrentSim, itemUUID, taskUUID,
                            (ScriptPermission) permissionMask);
                    };
                    break;
                case ScriptKeys.GETSCRIPTPERMISSIONREQUESTS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        List<string> csv = new List<string>();
                        object LockObject = new object();
                        lock (ScriptPermissionRequestLock)
                        {
                            Parallel.ForEach(ScriptPermissionRequests, o =>
                            {
                                lock (LockObject)
                                {
                                    csv.AddRange(new[] {wasGetStructureMemberDescription(o, o.Name), o.Name});
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o.Agent, o.Agent.FirstName), o.Agent.FirstName});
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o.Agent, o.Agent.LastName), o.Agent.LastName});
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o.Agent, o.Agent.UUID), o.Agent.UUID.ToString()});
                                    csv.AddRange(new[] {wasGetStructureMemberDescription(o, o.Item), o.Item.ToString()});
                                    csv.AddRange(new[] {wasGetStructureMemberDescription(o, o.Task), o.Task.ToString()});
                                    csv.Add(wasGetStructureMemberDescription(o, o.Permission));
                                    csv.AddRange(typeof (ScriptPermission).GetFields(BindingFlags.Public |
                                                                                     BindingFlags.Static)
                                        .Where(
                                            p =>
                                                !(((int) p.GetValue(null) &
                                                   (int) o.Permission)).Equals(0))
                                        .Select(p => p.Name).ToArray());
                                }
                            });
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.REPLYTOSCRIPTDIALOG:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        int channel;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.CHANNEL), message)),
                                out channel))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CHANNEL_SPECIFIED));
                        }
                        int index;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.INDEX), message)),
                                out index))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_BUTTON_INDEX_SPECIFIED));
                        }
                        string label =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.BUTTON),
                                message));
                        if (string.IsNullOrEmpty(label))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_BUTTON_SPECIFIED));
                        }
                        UUID itemUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                out itemUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_ITEM_SPECIFIED));
                        }
                        Client.Self.ReplyToScriptDialog(channel, index, label, itemUUID);
                    };
                    break;
                case ScriptKeys.GETSCRIPTDIALOGS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        List<string> csv = new List<string>();
                        object LockObject = new object();
                        lock (ScriptDialogLock)
                        {
                            Parallel.ForEach(ScriptDialogs, o =>
                            {
                                lock (LockObject)
                                {
                                    csv.AddRange(new[] {wasGetStructureMemberDescription(o, o.Message), o.Message});
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o.Agent, o.Agent.FirstName), o.Agent.FirstName});
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o.Agent, o.Agent.LastName), o.Agent.LastName});
                                    csv.AddRange(new[]
                                    {wasGetStructureMemberDescription(o.Agent, o.Agent.UUID), o.Agent.UUID.ToString()});
                                    csv.AddRange(new[]
                                    {
                                        wasGetStructureMemberDescription(o, o.Channel),
                                        o.Channel.ToString(CultureInfo.InvariantCulture)
                                    });
                                    csv.AddRange(new[] {wasGetStructureMemberDescription(o, o.Name), o.Name});
                                    csv.AddRange(new[] {wasGetStructureMemberDescription(o, o.Item), o.Item.ToString()});
                                    csv.Add(wasGetStructureMemberDescription(o, o.Button));
                                    csv.AddRange(o.Button.ToArray());
                                }
                            });
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.ANIMATION:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string item =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ITEM),
                                message));
                        if (string.IsNullOrEmpty(item))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_ITEM_SPECIFIED));
                        }
                        UUID itemUUID;
                        if (!UUID.TryParse(item, out itemUUID))
                        {
                            InventoryBase inventoryBaseItem =
                                FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, item
                                    ).FirstOrDefault();
                            if (inventoryBaseItem == null)
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                            }
                            itemUUID = inventoryBaseItem.UUID;
                        }
                        switch (
                            wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        message)).ToLowerInvariant()))
                        {
                            case Action.START:
                                Client.Self.AnimationStart(itemUUID, true);
                                break;
                            case Action.STOP:
                                Client.Self.AnimationStop(itemUUID, true);
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ANIMATION_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.PLAYGESTURE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string item =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ITEM),
                                message));
                        if (string.IsNullOrEmpty(item))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_ITEM_SPECIFIED));
                        }
                        UUID itemUUID;
                        if (!UUID.TryParse(item, out itemUUID))
                        {
                            InventoryBase inventoryBaseItem =
                                FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, item
                                    ).FirstOrDefault();
                            if (inventoryBaseItem == null)
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                            }
                            itemUUID = inventoryBaseItem.UUID;
                        }
                        Client.Self.PlayGesture(itemUUID);
                    };
                    break;
                case ScriptKeys.GETANIMATIONS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        List<string> csv = new List<string>();
                        Client.Self.SignaledAnimations.ForEach(
                            o =>
                                csv.AddRange(new List<string>
                                {
                                    o.Key.ToString(),
                                    o.Value.ToString(CultureInfo.InvariantCulture)
                                }));
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.RESTARTREGION:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_LAND_RIGHTS));
                        }
                        int delay;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.DELAY), message))
                                    .ToLowerInvariant(), out delay))
                        {
                            delay = LINDEN_CONSTANTS.ESTATE.REGION_RESTART_DELAY;
                        }
                        switch (
                            wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        message)).ToLowerInvariant()))
                        {
                            case Action.RESTART:
                                // Manually override Client.Estate.RestartRegion();
                                Client.Estate.EstateOwnerMessage(
                                    LINDEN_CONSTANTS.ESTATE.MESSAGES.REGION_RESTART_MESSAGE,
                                    delay.ToString(CultureInfo.InvariantCulture));
                                break;
                            case Action.CANCEL:
                                Client.Estate.CancelRestart();
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_RESTART_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.SETREGIONDEBUG:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_LAND_RIGHTS));
                        }
                        bool scripts;
                        if (
                            !bool.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.SCRIPTS), message))
                                    .ToLowerInvariant(), out scripts))
                        {
                            scripts = false;
                        }
                        bool collisions;
                        if (
                            !bool.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.COLLISIONS),
                                        message))
                                    .ToLowerInvariant(), out collisions))
                        {
                            collisions = false;
                        }
                        bool physics;
                        if (
                            !bool.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.PHYSICS), message))
                                    .ToLowerInvariant(), out physics))
                        {
                            physics = false;
                        }
                        Client.Estate.SetRegionDebug(!scripts, !collisions, !physics);
                    };
                    break;
                case ScriptKeys.GETREGIONTOP:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_LAND_RIGHTS));
                        }
                        int amount;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.AMOUNT), message)),
                                out amount))
                        {
                            amount = 5;
                        }
                        Dictionary<UUID, EstateTask> topTasks = new Dictionary<UUID, EstateTask>();
                        switch (
                            wasGetEnumValueFromDescription<Type>(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.TYPE), message))
                                    .ToLowerInvariant()))
                        {
                            case Type.SCRIPTS:
                                ManualResetEvent TopScriptsReplyEvent = new ManualResetEvent(false);
                                EventHandler<TopScriptsReplyEventArgs> TopScriptsReplyEventHandler = (sender, args) =>
                                {
                                    topTasks =
                                        args.Tasks.OrderByDescending(o => o.Value.Score)
                                            .ToDictionary(o => o.Key, o => o.Value);
                                    TopScriptsReplyEvent.Set();
                                };
                                lock (ServicesLock)
                                {
                                    Client.Estate.TopScriptsReply += TopScriptsReplyEventHandler;
                                    Client.Estate.RequestTopScripts();
                                    if (!TopScriptsReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                    {
                                        Client.Estate.TopScriptsReply -= TopScriptsReplyEventHandler;
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_TOP_SCRIPTS));
                                    }
                                    Client.Estate.TopScriptsReply -= TopScriptsReplyEventHandler;
                                }
                                break;
                            case Type.COLLIDERS:
                                ManualResetEvent TopCollidersReplyEvent = new ManualResetEvent(false);
                                EventHandler<TopCollidersReplyEventArgs> TopCollidersReplyEventHandler =
                                    (sender, args) =>
                                    {
                                        topTasks =
                                            args.Tasks.OrderByDescending(o => o.Value.Score)
                                                .ToDictionary(o => o.Key, o => o.Value);
                                        TopCollidersReplyEvent.Set();
                                    };
                                lock (ServicesLock)
                                {
                                    Client.Estate.TopCollidersReply += TopCollidersReplyEventHandler;
                                    Client.Estate.RequestTopScripts();
                                    if (!TopCollidersReplyEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                    {
                                        Client.Estate.TopCollidersReply -= TopCollidersReplyEventHandler;
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_TOP_SCRIPTS));
                                    }
                                    Client.Estate.TopCollidersReply -= TopCollidersReplyEventHandler;
                                }
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_TOP_TYPE));
                        }
                        List<string> data = new List<string>(topTasks.Take(amount).Select(o => new[]
                        {
                            o.Value.TaskName,
                            o.Key.ToString(),
                            o.Value.Score.ToString(CultureInfo.InvariantCulture),
                            o.Value.OwnerName,
                            o.Value.Position.ToString()
                        }).SelectMany(o => o));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.SETESTATELIST:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_LAND_RIGHTS));
                        }
                        bool allEstates;
                        if (
                            !bool.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ALL),
                                    message)),
                                out allEstates))
                        {
                            allEstates = false;
                        }
                        UUID targetUUID;
                        switch (
                            wasGetEnumValueFromDescription<Type>(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.TYPE), message))
                                    .ToLowerInvariant()))
                        {
                            case Type.BAN:
                                if (
                                    !UUID.TryParse(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                                message)), out targetUUID) && !AgentNameToUUID(
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                                            message)),
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME), message)),
                                                    Configuration.SERVICES_TIMEOUT, ref targetUUID))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                                }
                                switch (
                                    wasGetEnumValueFromDescription<Action>(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION), message))
                                            .ToLowerInvariant()))
                                {
                                    case Action.ADD:
                                        Client.Estate.BanUser(targetUUID, allEstates);
                                        break;
                                    case Action.REMOVE:
                                        Client.Estate.UnbanUser(targetUUID, allEstates);
                                        break;
                                    default:
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ESTATE_LIST_ACTION));
                                }
                                break;
                            case Type.GROUP:
                                if (
                                    !UUID.TryParse(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TARGET),
                                                message)),
                                        out targetUUID) && !GroupNameToUUID(
                                            wasUriUnescapeDataString(
                                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TARGET),
                                                    message)),
                                            Configuration.SERVICES_TIMEOUT, ref targetUUID))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                                }
                                switch (
                                    wasGetEnumValueFromDescription<Action>(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION), message))
                                            .ToLowerInvariant()))
                                {
                                    case Action.ADD:
                                        Client.Estate.AddAllowedGroup(targetUUID, allEstates);
                                        break;
                                    case Action.REMOVE:
                                        Client.Estate.RemoveAllowedGroup(targetUUID, allEstates);
                                        break;
                                    default:
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ESTATE_LIST_ACTION));
                                }
                                break;
                            case Type.USER:
                                if (
                                    !UUID.TryParse(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                                message)), out targetUUID) && !AgentNameToUUID(
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                                            message)),
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME), message)),
                                                    Configuration.SERVICES_TIMEOUT, ref targetUUID))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                                }
                                switch (
                                    wasGetEnumValueFromDescription<Action>(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION), message))
                                            .ToLowerInvariant()))
                                {
                                    case Action.ADD:
                                        Client.Estate.AddAllowedUser(targetUUID, allEstates);
                                        break;
                                    case Action.REMOVE:
                                        Client.Estate.RemoveAllowedUser(targetUUID, allEstates);
                                        break;
                                    default:
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ESTATE_LIST_ACTION));
                                }
                                break;
                            case Type.MANAGER:
                                if (
                                    !UUID.TryParse(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                                message)), out targetUUID) && !AgentNameToUUID(
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                                            message)),
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME), message)),
                                                    Configuration.SERVICES_TIMEOUT, ref targetUUID))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                                }
                                switch (
                                    wasGetEnumValueFromDescription<Action>(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION), message))
                                            .ToLowerInvariant()))
                                {
                                    case Action.ADD:
                                        Client.Estate.AddEstateManager(targetUUID, allEstates);
                                        break;
                                    case Action.REMOVE:
                                        Client.Estate.RemoveEstateManager(targetUUID, allEstates);
                                        break;
                                    default:
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ESTATE_LIST_ACTION));
                                }
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ESTATE_LIST));
                        }
                    };
                    break;
                case ScriptKeys.GETESTATELIST:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        if (!Client.Network.CurrentSim.IsEstateManager)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_LAND_RIGHTS));
                        }
                        int timeout;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TIMEOUT), message)),
                                out timeout))
                        {
                            timeout = Configuration.SERVICES_TIMEOUT;
                        }
                        List<UUID> estateList = new List<UUID>();
                        ManualResetEvent EstateListReplyEvent = new ManualResetEvent(false);
                        object LockObject = new object();
                        switch (
                            wasGetEnumValueFromDescription<Type>(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.TYPE), message))
                                    .ToLowerInvariant()))
                        {
                            case Type.BAN:
                                EventHandler<EstateBansReplyEventArgs> EstateBansReplyEventHandler = (sender, args) =>
                                {
                                    if (args.Count.Equals(0))
                                    {
                                        EstateListReplyEvent.Set();
                                        return;
                                    }
                                    lock (LockObject)
                                    {
                                        estateList.AddRange(args.Banned);
                                    }
                                };
                                lock (ServicesLock)
                                {
                                    Client.Estate.EstateBansReply += EstateBansReplyEventHandler;
                                    Client.Estate.RequestInfo();
                                    EstateListReplyEvent.WaitOne(timeout, false);
                                    Client.Estate.EstateBansReply -= EstateBansReplyEventHandler;
                                }
                                break;
                            case Type.GROUP:
                                EventHandler<EstateGroupsReplyEventArgs> EstateGroupsReplyEvenHandler =
                                    (sender, args) =>
                                    {
                                        if (args.Count.Equals(0))
                                        {
                                            EstateListReplyEvent.Set();
                                            return;
                                        }
                                        lock (LockObject)
                                        {
                                            estateList.AddRange(args.AllowedGroups);
                                        }
                                    };
                                lock (ServicesLock)
                                {
                                    Client.Estate.EstateGroupsReply += EstateGroupsReplyEvenHandler;
                                    Client.Estate.RequestInfo();
                                    EstateListReplyEvent.WaitOne(timeout, false);
                                    Client.Estate.EstateGroupsReply -= EstateGroupsReplyEvenHandler;
                                }
                                break;
                            case Type.MANAGER:
                                EventHandler<EstateManagersReplyEventArgs> EstateManagersReplyEventHandler =
                                    (sender, args) =>
                                    {
                                        if (args.Count.Equals(0))
                                        {
                                            EstateListReplyEvent.Set();
                                            return;
                                        }
                                        lock (LockObject)
                                        {
                                            estateList.AddRange(args.Managers);
                                        }
                                    };
                                lock (ServicesLock)
                                {
                                    Client.Estate.EstateManagersReply += EstateManagersReplyEventHandler;
                                    Client.Estate.RequestInfo();
                                    EstateListReplyEvent.WaitOne(timeout, false);
                                    Client.Estate.EstateManagersReply -= EstateManagersReplyEventHandler;
                                }
                                break;
                            case Type.USER:
                                EventHandler<EstateUsersReplyEventArgs> EstateUsersReplyEventHandler =
                                    (sender, args) =>
                                    {
                                        if (args.Count.Equals(0))
                                        {
                                            EstateListReplyEvent.Set();
                                            return;
                                        }
                                        lock (LockObject)
                                        {
                                            estateList.AddRange(args.AllowedUsers);
                                        }
                                    };
                                lock (ServicesLock)
                                {
                                    Client.Estate.EstateUsersReply += EstateUsersReplyEventHandler;
                                    Client.Estate.RequestInfo();
                                    EstateListReplyEvent.WaitOne(timeout, false);
                                    Client.Estate.EstateUsersReply -= EstateUsersReplyEventHandler;
                                }
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ESTATE_LIST));
                        }
                        lock (LockObject)
                        {
                            List<string> data = new List<string>(estateList.ConvertAll(o => o.ToString()));
                            if (!data.Count.Equals(0))
                            {
                                result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                    string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                        data.ToArray()));
                            }
                        }
                    };
                    break;
                case ScriptKeys.GETAVATARDATA:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        Avatar avatar = Client.Network.CurrentSim.ObjectsAvatars.Find(o => o.ID.Equals(agentUUID));
                        if (avatar == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AVATAR_NOT_IN_RANGE));
                        }
                        List<string> data = new List<string>(GetStructuredData(avatar,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message))));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETPRIMITIVES:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        uint entity =
                            (uint) wasGetEnumValueFromDescription<Entity>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ENTITY), message))
                                    .ToLowerInvariant());
                        Parcel parcel = null;
                        UUID agentUUID = UUID.Zero;
                        switch ((Entity) entity)
                        {
                            case Entity.REGION:
                                break;
                            case Entity.AVATAR:
                                if (
                                    !UUID.TryParse(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.AGENT),
                                                message)), out agentUUID) && !AgentNameToUUID(
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                                            message)),
                                                    wasUriUnescapeDataString(
                                                        wasKeyValueGet(
                                                            wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME), message)),
                                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                                }
                                break;
                            case Entity.PARCEL:
                                if (
                                    !GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                                }
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ENTITY));
                        }
                        List<string> csv = new List<string>();
                        object LockObject = new object();
                        HashSet<Primitive> primitives =
                            new HashSet<Primitive>(
                                Client.Network.CurrentSim.ObjectsPrimitives.FindAll(o => !o.ID.Equals(UUID.Zero)));
                        foreach (Primitive p in primitives)
                        {
                            ManualResetEvent ObjectPropertiesEvent = new ManualResetEvent(false);
                            EventHandler<ObjectPropertiesEventArgs> ObjectPropertiesEventHandler =
                                (sender, args) => ObjectPropertiesEvent.Set();
                            lock (ServicesLock)
                            {
                                Client.Objects.ObjectProperties += ObjectPropertiesEventHandler;
                                Client.Objects.SelectObjects(Client.Network.CurrentSim, new[] {p.LocalID}, true);
                                if (
                                    !ObjectPropertiesEvent.WaitOne(
                                        Configuration.SERVICES_TIMEOUT, false))
                                {
                                    Client.Objects.ObjectProperties -= ObjectPropertiesEventHandler;
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                                }
                                Client.Objects.ObjectProperties -= ObjectPropertiesEventHandler;
                            }
                            if (p.Properties == null) continue;
                            switch ((Entity) entity)
                            {
                                case Entity.REGION:
                                    break;
                                case Entity.AVATAR:
                                    Primitive parent = p;
                                    do
                                    {
                                        Primitive closure = parent;
                                        Primitive ancestor =
                                            Client.Network.CurrentSim.ObjectsPrimitives.Find(
                                                o => o.LocalID.Equals(closure.ParentID));
                                        if (ancestor == null) break;
                                        parent = ancestor;
                                    } while (!parent.ParentID.Equals(0));
                                    // check if an avatar is the parent of the parent primitive
                                    Avatar parentAvatar =
                                        Client.Network.CurrentSim.ObjectsAvatars.Find(
                                            o => o.LocalID.Equals(parent.ParentID));
                                    // parent avatar not found, this should not happen
                                    if (parentAvatar == null || !parentAvatar.ID.Equals(agentUUID)) continue;
                                    break;
                                case Entity.PARCEL:
                                    if (parcel == null) continue;
                                    Parcel primitiveParcel = null;
                                    if (!GetParcelAtPosition(Client.Network.CurrentSim, p.Position, ref primitiveParcel))
                                        continue;
                                    if (!primitiveParcel.LocalID.Equals(parcel.LocalID)) continue;
                                    break;
                            }
                            lock (LockObject)
                            {
                                csv.Add(p.Properties.Name);
                                csv.Add(p.ID.ToString());
                            }
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETAVATARPOSITIONS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        uint entity =
                            (uint) wasGetEnumValueFromDescription<Entity>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ENTITY), message))
                                    .ToLowerInvariant());
                        Parcel parcel = null;
                        switch ((Entity) entity)
                        {
                            case Entity.REGION:
                                break;
                            case Entity.PARCEL:
                                if (
                                    !GetParcelAtPosition(Client.Network.CurrentSim, position, ref parcel))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_FIND_PARCEL));
                                }
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ENTITY));
                        }
                        List<string> csv = new List<string>();
                        Dictionary<UUID, Vector3> avatarPositions = new Dictionary<UUID, Vector3>();
                        Client.Network.CurrentSim.AvatarPositions.ForEach(o => avatarPositions.Add(o.Key, o.Value));
                        foreach (KeyValuePair<UUID, Vector3> p in avatarPositions)
                        {
                            string name = string.Empty;
                            if (!AgentUUIDToName(p.Key, Configuration.SERVICES_TIMEOUT, ref name))
                                continue;
                            switch ((Entity) entity)
                            {
                                case Entity.REGION:
                                    break;
                                case Entity.PARCEL:
                                    if (parcel == null) return;
                                    Parcel avatarParcel = null;
                                    if (!GetParcelAtPosition(Client.Network.CurrentSim, p.Value, ref avatarParcel))
                                        continue;
                                    if (!avatarParcel.LocalID.Equals(parcel.LocalID)) continue;
                                    break;
                            }
                            csv.Add(name);
                            csv.Add(p.Key.ToString());
                            csv.Add(p.Value.ToString());
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETMAPAVATARPOSITIONS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string region =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.REGION),
                                message));
                        if (string.IsNullOrEmpty(region))
                        {
                            region = Client.Network.CurrentSim.Name;
                        }
                        ManualResetEvent GridRegionEvent = new ManualResetEvent(false);
                        ulong regionHandle = 0;
                        EventHandler<GridRegionEventArgs> GridRegionEventHandler = (sender, args) =>
                        {
                            regionHandle = args.Region.RegionHandle;
                            GridRegionEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Grid.GridRegion += GridRegionEventHandler;
                            Client.Grid.RequestMapRegion(region, GridLayerType.Objects);
                            if (!GridRegionEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Grid.GridRegion -= GridRegionEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_REGION));
                            }
                            Client.Grid.GridRegion -= GridRegionEventHandler;
                        }
                        if (regionHandle.Equals(0))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.REGION_NOT_FOUND));
                        }
                        HashSet<MapItem> mapItems =
                            new HashSet<MapItem>(Client.Grid.MapItems(regionHandle, GridItemType.AgentLocations,
                                GridLayerType.Objects, Configuration.SERVICES_TIMEOUT));
                        if (mapItems.Count.Equals(0))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_MAP_ITEMS_FOUND));
                        }
                        List<string> data =
                            new List<string>(mapItems.Where(o => (o as MapAgentLocation) != null).Select(o => new[]
                            {
                                ((MapAgentLocation) o).AvatarCount.ToString(CultureInfo.InvariantCulture),
                                new Vector3(o.LocalX, o.LocalY, 0).ToString()
                            }).SelectMany(o => o));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETSELFDATA:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        List<string> data = new List<string>(GetStructuredData(Client.Self,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message))));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.DISPLAYNAME:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string previous = string.Empty;
                        Client.Avatars.GetDisplayNames(new List<UUID> {Client.Self.AgentID},
                            (succeded, names, IDs) =>
                            {
                                if (!succeded || names.Length < 1)
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.FAILED_TO_GET_DISPLAY_NAME));
                                }
                                previous = names[0].DisplayName;
                            });
                        switch (
                            wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        message)).ToLowerInvariant()))
                        {
                            case Action.GET:
                                result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA), previous);
                                break;
                            case Action.SET:
                                string name =
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.NAME), message));
                                if (string.IsNullOrEmpty(name))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_NAME_PROVIDED));
                                }
                                bool succeeded = true;
                                ManualResetEvent SetDisplayNameEvent = new ManualResetEvent(false);
                                EventHandler<SetDisplayNameReplyEventArgs> SetDisplayNameEventHandler =
                                    (sender, args) =>
                                    {
                                        succeeded = args.Status.Equals(LINDEN_CONSTANTS.AVATARS.SET_DISPLAY_NAME_SUCCESS);
                                        SetDisplayNameEvent.Set();
                                    };
                                lock (ServicesLock)
                                {
                                    Client.Self.SetDisplayNameReply += SetDisplayNameEventHandler;
                                    Client.Self.SetDisplayName(previous, name);
                                    if (!SetDisplayNameEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                    {
                                        Client.Self.SetDisplayNameReply -= SetDisplayNameEventHandler;
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_WAITING_FOR_ESTATE_LIST));
                                    }
                                    Client.Self.SetDisplayNameReply -= SetDisplayNameEventHandler;
                                }
                                if (!succeeded)
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.COULD_NOT_SET_DISPLAY_NAME));
                                }
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.GETINVENTORYOFFERS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INVENTORY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        object LockObject = new object();
                        List<string> csv = new List<string>();
                        lock (InventoryOffersLock)
                        {
                            Parallel.ForEach(InventoryOffers, o =>
                            {
                                List<string> name =
                                    new List<string>(
                                        GetAvatarNames(o.Key.Offer.FromAgentName));
                                lock (LockObject)
                                {
                                    csv.AddRange(new[]
                                    {wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME), name.First()});
                                    csv.AddRange(new[]
                                    {wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME), name.Last()});
                                    csv.AddRange(new[]
                                    {wasGetDescriptionFromEnumValue(ScriptKeys.TYPE), o.Key.AssetType.ToString()});
                                    csv.AddRange(new[]
                                    {wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE), o.Key.Offer.Message});
                                    csv.AddRange(new[]
                                    {
                                        wasGetDescriptionFromEnumValue(ScriptKeys.SESSION),
                                        o.Key.Offer.IMSessionID.ToString()
                                    });
                                }
                            });
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.REPLYTOINVENTORYOFFER:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INVENTORY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID session;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.SESSION), message)),
                                out session))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_SESSION_SPECIFIED));
                        }
                        lock (InventoryOffersLock)
                        {
                            if (!InventoryOffers.Any(o => o.Key.Offer.IMSessionID.Equals(session)))
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_OFFER_NOT_FOUND));
                            }
                        }
                        KeyValuePair<InventoryObjectOfferedEventArgs, ManualResetEvent> offer;
                        lock (InventoryOffersLock)
                        {
                            offer =
                                InventoryOffers.FirstOrDefault(o => o.Key.Offer.IMSessionID.Equals(session));
                        }
                        UUID folderUUID;
                        string folder =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FOLDER),
                                message));
                        if (string.IsNullOrEmpty(folder) || !UUID.TryParse(folder, out folderUUID))
                        {
                            folderUUID =
                                Client.Inventory.Store.Items[Client.Inventory.FindFolderForType(offer.Key.AssetType)]
                                    .Data.UUID;
                        }
                        if (folderUUID.Equals(UUID.Zero))
                        {
                            InventoryBase inventoryBaseItem =
                                FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, folder
                                    ).FirstOrDefault();
                            if (inventoryBaseItem != null)
                            {
                                InventoryItem item = inventoryBaseItem as InventoryItem;
                                if (item != null && item.AssetType.Equals(AssetType.Folder))
                                {
                                    folderUUID = inventoryBaseItem.UUID;
                                }
                            }
                        }
                        switch (
                            wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        message)).ToLowerInvariant()))
                        {
                            case Action.ACCEPT:
                                lock (InventoryOffersLock)
                                {
                                    if (!folderUUID.Equals(UUID.Zero))
                                    {
                                        offer.Key.FolderID = folderUUID;
                                    }
                                    offer.Key.Accept = true;
                                    offer.Value.Set();
                                }
                                break;
                            case Action.DECLINE:
                                lock (InventoryOffersLock)
                                {
                                    offer.Key.Accept = false;
                                    offer.Value.Set();
                                }
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.GETFRIENDSLIST:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_FRIENDSHIP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        List<string> csv = new List<string>();
                        Client.Friends.FriendList.ForEach(o =>
                        {
                            csv.Add(o.Name);
                            csv.Add(o.UUID.ToString());
                        });
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.GETFRIENDSHIPREQUESTS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_FRIENDSHIP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        List<string> csv = new List<string>();
                        Client.Friends.FriendRequests.ForEach(o =>
                        {
                            string name = string.Empty;
                            if (!AgentUUIDToName(o.Key, Configuration.SERVICES_TIMEOUT, ref name))
                            {
                                return;
                            }
                            csv.Add(name);
                            csv.Add(o.Key.ToString());
                        });
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.REPLYTOFRIENDSHIPREQUEST:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_FRIENDSHIP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        UUID session = UUID.Zero;
                        Client.Friends.FriendRequests.ForEach(o =>
                        {
                            if (o.Key.Equals(agentUUID))
                            {
                                session = o.Value;
                            }
                        });
                        if (session.Equals(UUID.Zero))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_FRIENDSHIP_OFFER_FOUND));
                        }
                        switch (
                            wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                        message)).ToLowerInvariant()))
                        {
                            case Action.ACCEPT:
                                Client.Friends.AcceptFriendship(agentUUID, session);
                                break;
                            case Action.DECLINE:
                                Client.Friends.DeclineFriendship(agentUUID, session);
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.GETFRIENDDATA:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_FRIENDSHIP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        FriendInfo friend = Client.Friends.FriendList.Find(o => o.UUID.Equals(agentUUID));
                        if (friend == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.FRIEND_NOT_FOUND));
                        }
                        List<string> data = new List<string>(GetStructuredData(friend,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message))));
                        if (!data.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    data.ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.OFFERFRIENDSHIP:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_FRIENDSHIP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        FriendInfo friend = Client.Friends.FriendList.Find(o => o.UUID.Equals(agentUUID));
                        if (friend != null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_ALREADY_FRIEND));
                        }
                        Client.Friends.OfferFriendship(agentUUID,
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.MESSAGE),
                                message)));
                    };
                    break;
                case ScriptKeys.TERMINATEFRIENDSHIP:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_FRIENDSHIP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        FriendInfo friend = Client.Friends.FriendList.Find(o => o.UUID.Equals(agentUUID));
                        if (friend == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.FRIEND_NOT_FOUND));
                        }
                        Client.Friends.TerminateFriendship(agentUUID);
                    };
                    break;
                case ScriptKeys.GRANTFRIENDRIGHTS:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_FRIENDSHIP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        FriendInfo friend = Client.Friends.FriendList.Find(o => o.UUID.Equals(agentUUID));
                        if (friend == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.FRIEND_NOT_FOUND));
                        }
                        int rights = 0;
                        Parallel.ForEach(
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.RIGHTS),
                                message))
                                .Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER}, StringSplitOptions.RemoveEmptyEntries),
                            o =>
                                Parallel.ForEach(
                                    typeof (FriendRights).GetFields(BindingFlags.Public | BindingFlags.Static)
                                        .Where(p => p.Name.Equals(o, StringComparison.Ordinal)),
                                    q => { rights |= ((int) q.GetValue(null)); }));
                        Client.Friends.GrantRights(agentUUID, (FriendRights) rights);
                    };
                    break;
                case ScriptKeys.MAPFRIEND:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_FRIENDSHIP))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.AGENT), message)),
                                out agentUUID) && !AgentNameToUUID(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FIRSTNAME),
                                            message)),
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.LASTNAME),
                                            message)),
                                    Configuration.SERVICES_TIMEOUT, ref agentUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.AGENT_NOT_FOUND));
                        }
                        FriendInfo friend = Client.Friends.FriendList.Find(o => o.UUID.Equals(agentUUID));
                        if (friend == null)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.FRIEND_NOT_FOUND));
                        }
                        if (!friend.CanSeeThemOnMap)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.FRIEND_DOES_NOT_ALLOW_MAPPING));
                        }
                        ulong regionHandle = 0;
                        Vector3 position = Vector3.Zero;
                        ManualResetEvent FriendFoundEvent = new ManualResetEvent(false);
                        bool offline = false;
                        EventHandler<FriendFoundReplyEventArgs> FriendFoundEventHandler = (sender, args) =>
                        {
                            if (args.RegionHandle.Equals(0))
                            {
                                offline = true;
                                FriendFoundEvent.Set();
                                return;
                            }
                            regionHandle = args.RegionHandle;
                            position = args.Location;
                            FriendFoundEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Friends.FriendFoundReply += FriendFoundEventHandler;
                            Client.Friends.MapFriend(agentUUID);
                            if (!FriendFoundEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Friends.FriendFoundReply -= FriendFoundEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_MAPPING_FRIEND));
                            }
                            Client.Friends.FriendFoundReply -= FriendFoundEventHandler;
                        }
                        if (offline)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.FRIEND_OFFLINE));
                        }
                        UUID parcelUUID = Client.Parcels.RequestRemoteParcelID(position, regionHandle, UUID.Zero);
                        ManualResetEvent ParcelInfoEvent = new ManualResetEvent(false);
                        string regionName = string.Empty;
                        EventHandler<ParcelInfoReplyEventArgs> ParcelInfoEventHandler = (sender, args) =>
                        {
                            regionName = args.Parcel.SimName;
                            ParcelInfoEvent.Set();
                        };
                        lock (ServicesLock)
                        {
                            Client.Parcels.ParcelInfoReply += ParcelInfoEventHandler;
                            Client.Parcels.RequestParcelInfo(parcelUUID);
                            if (!ParcelInfoEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                            {
                                Client.Parcels.ParcelInfoReply -= ParcelInfoEventHandler;
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_GETTING_PARCELS));
                            }
                            Client.Parcels.ParcelInfoReply -= ParcelInfoEventHandler;
                        }
                        result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                            string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, new[] {regionName, position.ToString()}));
                    };
                    break;
                case ScriptKeys.SETOBJECTPERMISSIONS:
                    execute = () =>
                    {
                        if (
                            !HasCorradePermission(group,
                                (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        byte who = 0;
                        Parallel.ForEach(
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.WHO),
                                message))
                                .Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER}, StringSplitOptions.RemoveEmptyEntries),
                            o =>
                                Parallel.ForEach(
                                    typeof (PermissionWho).GetFields(BindingFlags.Public | BindingFlags.Static)
                                        .Where(p => p.Name.Equals(o, StringComparison.Ordinal)),
                                    q => { who |= ((byte) q.GetValue(null)); }));
                        uint permissions = 0;
                        Parallel.ForEach(
                            wasUriUnescapeDataString(
                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.PERMISSIONS), message))
                                .Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER}, StringSplitOptions.RemoveEmptyEntries),
                            o =>
                                Parallel.ForEach(
                                    typeof (PermissionMask).GetFields(BindingFlags.Public | BindingFlags.Static)
                                        .Where(p => p.Name.Equals(o, StringComparison.Ordinal)),
                                    q => { permissions |= ((uint) q.GetValue(null)); }));
                        Client.Objects.SetPermissions(Client.Network.CurrentSim, new List<uint> {primitive.LocalID},
                            (PermissionWho) who, (PermissionMask) permissions, true);
                    };
                    break;
                case ScriptKeys.OBJECTDEED:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        Client.Objects.DeedObject(Client.Network.CurrentSim, primitive.LocalID, groupUUID);
                    };
                    break;
                case ScriptKeys.SETOBJECTGROUP:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        UUID groupUUID =
                            Configuration.GROUPS.FirstOrDefault(
                                o => o.Name.Equals(group, StringComparison.Ordinal)).UUID;
                        if (groupUUID.Equals(UUID.Zero) &&
                            !GroupNameToUUID(group, Configuration.SERVICES_TIMEOUT, ref groupUUID))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.GROUP_NOT_FOUND));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        Client.Objects.SetObjectsGroup(Client.Network.CurrentSim, new List<uint> {primitive.LocalID},
                            groupUUID);
                    };
                    break;
                case ScriptKeys.SETOBJECTSALEINFO:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        int price;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.PRICE), message)),
                                out price))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_PRICE));
                        }
                        if (price < 0)
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_PRICE));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        FieldInfo saleTypeInfo = typeof (SaleType).GetFields(BindingFlags.Public |
                                                                             BindingFlags.Static)
                            .FirstOrDefault(o =>
                                o.Name.Equals(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TYPE), message)),
                                    StringComparison.Ordinal));
                        Client.Objects.SetSaleInfo(Client.Network.CurrentSim, primitive.LocalID, saleTypeInfo != null
                            ? (SaleType)
                                saleTypeInfo.GetValue(null)
                            : SaleType.Copy, price);
                    };
                    break;
                case ScriptKeys.SETOBJECTPOSITION:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        Client.Objects.SetPosition(Client.Network.CurrentSim, primitive.LocalID, position);
                    };
                    break;
                case ScriptKeys.SETOBJECTROTATION:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        Quaternion rotation;
                        if (
                            !Quaternion.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ROTATION), message)),
                                out rotation))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_ROTATION));
                        }
                        Client.Objects.SetRotation(Client.Network.CurrentSim, primitive.LocalID, rotation);
                    };
                    break;
                case ScriptKeys.SETOBJECTNAME:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        string name =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.NAME),
                                message));
                        if (string.IsNullOrEmpty(name))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_NAME_PROVIDED));
                        }
                        Client.Objects.SetName(Client.Network.CurrentSim, primitive.LocalID, name);
                    };
                    break;
                case ScriptKeys.SETOBJECTDESCRIPTION:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        float range;
                        if (
                            !float.TryParse(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.RANGE), message)),
                                out range))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_RANGE_PROVIDED));
                        }
                        Primitive primitive = null;
                        if (
                            !FindPrimitive(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.ITEM), message)),
                                range,
                                Configuration.SERVICES_TIMEOUT,
                                ref primitive))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.PRIMITIVE_NOT_FOUND));
                        }
                        string description =
                            wasUriUnescapeDataString(
                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DESCRIPTION), message));
                        if (string.IsNullOrEmpty(description))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_DESCRIPTION_PROVIDED));
                        }
                        Client.Objects.SetDescription(Client.Network.CurrentSim, primitive.LocalID, description);
                    };
                    break;
                case ScriptKeys.CHANGEAPPEARANCE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_GROOMING))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string folder =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FOLDER),
                                message));
                        if (string.IsNullOrEmpty(folder))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_FOLDER_SPECIFIED));
                        }
                        // Check for items that can be worn.
                        List<InventoryBase> items =
                            GetInventoryFolderContents<InventoryBase>(Client.Inventory.Store.RootNode, folder)
                                .Where(CanBeWorn)
                                .ToList();
                        if (items.Count.Equals(0))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_EQUIPABLE_ITEMS));
                        }
                        // Now remove the current outfit items.
                        List<InventoryBase> outfitFolderContents;
                        lock (InventoryLock)
                        {
                            outfitFolderContents =
                                Client.Inventory.Store.GetContents(
                                    GetOrCreateOutfitFolder(Configuration.SERVICES_TIMEOUT));
                        }
                        outfitFolderContents.FindAll(
                            o => CanBeWorn(o) && ((InventoryItem) o).AssetType.Equals(AssetType.Link))
                            .ForEach(p =>
                            {
                                InventoryItem item = ResolveItemLink(p as InventoryItem);
                                if (item as InventoryWearable != null)
                                {
                                    if (!IsBodyPart(item))
                                    {
                                        UnWear(item, Configuration.SERVICES_TIMEOUT);
                                        return;
                                    }
                                    if (items.Any(q =>
                                    {
                                        InventoryWearable i = q as InventoryWearable;
                                        return i != null &&
                                               ((InventoryWearable) item).WearableType.Equals(i.WearableType);
                                    })) UnWear(item, Configuration.SERVICES_TIMEOUT);
                                    return;
                                }
                                if (item is InventoryAttachment || item is InventoryObject)
                                {
                                    Detach(item, Configuration.SERVICES_TIMEOUT);
                                }
                            });
                        // And equip the specified folder.
                        Parallel.ForEach(items, o =>
                        {
                            InventoryItem item = o as InventoryItem;
                            if (item is InventoryWearable)
                            {
                                Wear(item, false, Configuration.SERVICES_TIMEOUT);
                                return;
                            }
                            if (item is InventoryAttachment || item is InventoryObject)
                            {
                                Attach(item, AttachmentPoint.Default, false, Configuration.SERVICES_TIMEOUT);
                            }
                        });
                        // And rebake.
                        if (!Rebake(Configuration.SERVICES_TIMEOUT, Configuration.REBAKE_DELAY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.REBAKE_FAILED));
                        }
                    };
                    break;
                case ScriptKeys.PLAYSOUND:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_INTERACT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        Vector3 position;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.POSITION), message)),
                                out position))
                        {
                            position = Client.Self.SimPosition;
                        }
                        float gain;
                        if (!float.TryParse(
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.GAIN),
                                message)),
                            out gain))
                        {
                            gain = 1;
                        }
                        UUID itemUUID;
                        string item =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ITEM),
                                message));
                        if (!UUID.TryParse(item, out itemUUID))
                        {
                            InventoryBase inventoryBaseItem =
                                FindInventory<InventoryBase>(Client.Inventory.Store.RootNode, item
                                    ).FirstOrDefault();
                            if (inventoryBaseItem == null)
                            {
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVENTORY_ITEM_NOT_FOUND));
                            }
                            itemUUID = inventoryBaseItem.UUID;
                        }
                        Client.Sound.SendSoundTrigger(itemUUID, position, gain);
                    };
                    break;
                case ScriptKeys.TERRAIN:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        byte[] data = null;
                        switch (wasGetEnumValueFromDescription<Action>(
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                message))
                                .ToLowerInvariant()))
                        {
                            case Action.GET:
                                ManualResetEvent[] DownloadTerrainEvents =
                                {
                                    new ManualResetEvent(false),
                                    new ManualResetEvent(false)
                                };
                                EventHandler<InitiateDownloadEventArgs> InitiateDownloadEventHandler =
                                    (sender, args) =>
                                    {
                                        Client.Assets.RequestAssetXfer(args.SimFileName, false, false, UUID.Zero,
                                            AssetType.Unknown, false);
                                        DownloadTerrainEvents[0].Set();
                                    };
                                EventHandler<XferReceivedEventArgs> XferReceivedEventHandler = (sender, args) =>
                                {
                                    data = args.Xfer.AssetData;
                                    DownloadTerrainEvents[1].Set();
                                };
                                lock (ServicesLock)
                                {
                                    Client.Assets.InitiateDownload += InitiateDownloadEventHandler;
                                    Client.Assets.XferReceived += XferReceivedEventHandler;
                                    Client.Estate.EstateOwnerMessage("terrain", new List<string>
                                    {
                                        "download filename",
                                        Client.Network.CurrentSim.Name
                                    });
                                    if (!WaitHandle.WaitAll(DownloadTerrainEvents.Select(o => (WaitHandle) o).ToArray(),
                                        Configuration.SERVICES_TIMEOUT, false))
                                    {
                                        Client.Assets.InitiateDownload -= InitiateDownloadEventHandler;
                                        Client.Assets.XferReceived -= XferReceivedEventHandler;
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_DOWNLOADING_ASSET));
                                    }
                                    Client.Assets.InitiateDownload -= InitiateDownloadEventHandler;
                                    Client.Assets.XferReceived -= XferReceivedEventHandler;
                                }
                                if (data == null || !data.Length.Equals(0))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.EMPTY_ASSET_DATA));
                                }
                                result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA), Convert.ToBase64String(data));
                                break;
                            case Action.SET:
                                try
                                {
                                    data = Convert.FromBase64String(
                                        wasUriUnescapeDataString(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                                message)));
                                }
                                catch (Exception)
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.INVALID_ASSET_DATA));
                                }
                                if (data == null || !data.Length.Equals(0))
                                {
                                    throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.EMPTY_ASSET_DATA));
                                }
                                ManualResetEvent AssetUploadEvent = new ManualResetEvent(false);
                                EventHandler<AssetUploadEventArgs> AssetUploadEventHandler = (sender, args) =>
                                {
                                    if (args.Upload.Transferred.Equals(args.Upload.Size))
                                    {
                                        AssetUploadEvent.Set();
                                    }
                                };
                                lock (ServicesLock)
                                {
                                    Client.Assets.UploadProgress += AssetUploadEventHandler;
                                    Client.Estate.UploadTerrain(data, Client.Network.CurrentSim.Name);
                                    if (!AssetUploadEvent.WaitOne(Configuration.SERVICES_TIMEOUT, false))
                                    {
                                        Client.Assets.UploadProgress -= AssetUploadEventHandler;
                                        throw new Exception(
                                            wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_UPLOADING_ASSET));
                                    }
                                    Client.Assets.UploadProgress -= AssetUploadEventHandler;
                                }
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.GETTERRAINHEIGHT:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_LAND))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        Vector3 southwest;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.SOUTHWEST),
                                        message)),
                                out southwest))
                        {
                            southwest = new Vector3(0, 0, 0);
                        }
                        Vector3 northeast;
                        if (
                            !Vector3.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.NORTHEAST),
                                        message)),
                                out northeast))
                        {
                            northeast = new Vector3(255, 255, 0);
                        }

                        int x1 = Convert.ToInt32(southwest.X);
                        int y1 = Convert.ToInt32(southwest.Y);
                        int x2 = Convert.ToInt32(northeast.X);
                        int y2 = Convert.ToInt32(northeast.Y);

                        if (x1 > x2)
                        {
                            wasXORSwap(ref x1, ref x2);
                        }
                        if (y1 > y2)
                        {
                            wasXORSwap(ref y1, ref y2);
                        }

                        int sx = x2 - x1 + 1;
                        int sy = y2 - y1 + 1;

                        float[] csv = new float[sx*sy];
                        Parallel.ForEach(Enumerable.Range(x1, sx), x => Parallel.ForEach(Enumerable.Range(y1, sy), y =>
                        {
                            float height;
                            csv[2*(x2 - x) + (y2 - y)] = Client.Network.CurrentSim.TerrainHeightAtPoint(x, y, out height)
                                ? height
                                : -1;
                        }));

                        if (!csv.Length.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER,
                                    csv.Select(o => o.ToString(CultureInfo.InvariantCulture)).ToArray()));
                        }
                    };
                    break;
                case ScriptKeys.CROUCH:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        uint action =
                            (uint) wasGetEnumValueFromDescription<Action>(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION), message))
                                    .ToLowerInvariant());
                        switch ((Action) action)
                        {
                            case Action.START:
                            case Action.STOP:
                                if (Client.Self.Movement.SitOnGround || !Client.Self.SittingOn.Equals(0))
                                {
                                    Client.Self.Stand();
                                }
                                Client.Self.SignaledAnimations.ForEach(
                                    o => Client.Self.AnimationStop(o.Key, true));
                                Client.Self.Crouch(action.Equals((uint) Action.START));
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.FLY_ACTION_START_OR_STOP));
                        }
                    };
                    break;
                case ScriptKeys.JUMP:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_MOVEMENT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        uint action =
                            (uint) wasGetEnumValueFromDescription<Action>(wasUriUnescapeDataString(
                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION), message))
                                .ToLowerInvariant());
                        switch ((Action) action)
                        {
                            case Action.START:
                            case Action.STOP:
                                if (Client.Self.Movement.SitOnGround || !Client.Self.SittingOn.Equals(0))
                                {
                                    Client.Self.Stand();
                                }
                                Client.Self.SignaledAnimations.ForEach(
                                    o => Client.Self.AnimationStop(o.Key, true));
                                Client.Self.Jump(action.Equals((uint) Action.START));
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.FLY_ACTION_START_OR_STOP));
                        }
                    };
                    break;
                case ScriptKeys.EXECUTE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_EXECUTE))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        string file =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.FILE),
                                message));
                        if (string.IsNullOrEmpty(file))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_EXECUTABLE_FILE_PROVIDED));
                        }
                        ProcessStartInfo p = new ProcessStartInfo(file,
                            wasUriUnescapeDataString(wasKeyValueGet(
                                wasGetDescriptionFromEnumValue(ScriptKeys.PARAMETER), message)))
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            WindowStyle = ProcessWindowStyle.Normal,
                            UseShellExecute = false
                        };
                        StringBuilder stdout = new StringBuilder();
                        StringBuilder stderr = new StringBuilder();
                        Process q = Process.Start(p);
                        ManualResetEvent[] StdEvent =
                        {
                            new ManualResetEvent(false),
                            new ManualResetEvent(false)
                        };
                        q.OutputDataReceived += (sender, output) =>
                        {
                            if (output.Data == null)
                            {
                                StdEvent[0].Set();
                                return;
                            }
                            stdout.AppendLine(output.Data);
                        };
                        q.ErrorDataReceived += (sender, output) =>
                        {
                            if (output.Data == null)
                            {
                                StdEvent[1].Set();
                                return;
                            }
                            stderr.AppendLine(output.Data);
                        };
                        q.BeginErrorReadLine();
                        q.BeginOutputReadLine();
                        if (!q.WaitForExit(Configuration.SERVICES_TIMEOUT))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.TIMEOUT_WAITING_FOR_EXECUTION));
                        }
                        if (StdEvent[0].WaitOne(Configuration.SERVICES_TIMEOUT) && !stdout.Length.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA), stdout.ToString());
                        }
                        if (StdEvent[1].WaitOne(Configuration.SERVICES_TIMEOUT) && !stderr.Length.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA), stderr.ToString());
                        }
                    };
                    break;
                case ScriptKeys.CONFIGURATION:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_SYSTEM))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        switch (wasGetEnumValueFromDescription<Action>(wasUriUnescapeDataString(
                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION), message))
                            .ToLowerInvariant()))
                        {
                            case Action.READ:
                                result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                    Convert.ToBase64String(
                                        Encoding.ASCII.GetBytes(Configuration.Read(CORRADE_CONSTANTS.CONFIGURATION_FILE))));
                                break;
                            case Action.WRITE:
                                Configuration.Write(CORRADE_CONSTANTS.CONFIGURATION_FILE,
                                    Encoding.ASCII.GetString(
                                        Convert.FromBase64String(
                                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                                message))));
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.CACHE:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_SYSTEM))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        switch (wasGetEnumValueFromDescription<Action>(wasUriUnescapeDataString(
                            wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION), message))
                            .ToLowerInvariant()))
                        {
                            case Action.PURGE:
                                Cache.Purge();
                                break;
                            case Action.SAVE:
                                SaveCorradeCache.Invoke();
                                break;
                            case Action.LOAD:
                                LoadCorradeCache.Invoke();
                                break;
                            default:
                                throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_ACTION));
                        }
                    };
                    break;
                case ScriptKeys.LOGOUT:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_SYSTEM))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        ConnectionSemaphores.FirstOrDefault(o => o.Key.Equals('u')).Value.Set();
                    };
                    break;
                case ScriptKeys.RLV:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_SYSTEM))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        switch (wasGetEnumValueFromDescription<Action>(
                            wasUriUnescapeDataString(
                                wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION),
                                    message)).ToLowerInvariant()))
                        {
                            case Action.ENABLE:
                                EnableCorradeRLV = true;
                                break;
                            case Action.DISABLE:
                                EnableCorradeRLV = false;
                                RLVRules.Clear();
                                break;
                        }
                    };
                    break;
                case ScriptKeys.VERSION:
                    execute = () => result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA), CORRADE_VERSION);
                    break;
                case ScriptKeys.DIRECTORYSEARCH:
                    execute = () =>
                    {
                        if (!HasCorradePermission(group, (int) Permissions.PERMISSION_DIRECTORY))
                        {
                            throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.NO_CORRADE_PERMISSIONS));
                        }
                        int timeout;
                        if (
                            !int.TryParse(
                                wasUriUnescapeDataString(
                                    wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.TIMEOUT), message)),
                                out timeout))
                        {
                            timeout = Configuration.SERVICES_TIMEOUT;
                        }
                        object LockObject = new object();
                        List<string> csv = new List<string>();
                        int handledEvents = 0;
                        int counter = 1;
                        string name =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.NAME),
                                message));
                        string fields =
                            wasUriUnescapeDataString(wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA),
                                message));
                        switch (
                            wasGetEnumValueFromDescription<Type>(
                                wasUriUnescapeDataString(wasKeyValueGet(
                                    wasGetDescriptionFromEnumValue(ScriptKeys.TYPE), message))
                                    .ToLowerInvariant()))
                        {
                            case Type.CLASSIFIED:
                                DirectoryManager.Classified searchClassified = new DirectoryManager.Classified();
                                wasCSVToStructure(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA), message)),
                                    ref searchClassified);
                                Dictionary<DirectoryManager.Classified, int> classifieds =
                                    new Dictionary<DirectoryManager.Classified, int>();
                                ManualResetEvent DirClassifiedsEvent = new ManualResetEvent(false);
                                EventHandler<DirClassifiedsReplyEventArgs> DirClassifiedsEventHandler =
                                    (sender, args) => Parallel.ForEach(args.Classifieds, o =>
                                    {
                                        int score = !string.IsNullOrEmpty(fields)
                                            ? wasGetFields(searchClassified, searchClassified.GetType().Name)
                                                .Sum(
                                                    p =>
                                                        (from q in
                                                            wasGetFields(o,
                                                                o.GetType().Name)
                                                            let r = wasGetInfoValue(p.Key, p.Value)
                                                            where r != null
                                                            let s = wasGetInfoValue(q.Key, q.Value)
                                                            where s != null
                                                            where r.Equals(s)
                                                            select r).Count())
                                            : 0;
                                        lock (LockObject)
                                        {
                                            classifieds.Add(o, score);
                                        }
                                    });
                                lock (ServicesLock)
                                {
                                    Client.Directory.DirClassifiedsReply += DirClassifiedsEventHandler;
                                    Client.Directory.StartClassifiedSearch(name);
                                    DirClassifiedsEvent.WaitOne(timeout, false);
                                    DirClassifiedsEvent.Close();
                                    Client.Directory.DirClassifiedsReply -= DirClassifiedsEventHandler;
                                }
                                DirectoryManager.Classified topClassified =
                                    classifieds.OrderByDescending(o => o.Value).FirstOrDefault().Key;
                                Parallel.ForEach(
                                    wasGetFields(topClassified, topClassified.GetType().Name),
                                    o =>
                                    {
                                        lock (LockObject)
                                        {
                                            csv.Add(o.Key.Name);
                                            csv.AddRange(wasGetInfo(o.Key, o.Value));
                                        }
                                    });
                                break;
                            case Type.EVENT:
                                DirectoryManager.EventsSearchData searchEvent = new DirectoryManager.EventsSearchData();
                                wasCSVToStructure(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA), message)),
                                    ref searchEvent);
                                Dictionary<DirectoryManager.EventsSearchData, int> events =
                                    new Dictionary<DirectoryManager.EventsSearchData, int>();
                                ManualResetEvent DirEventsReplyEvent = new ManualResetEvent(false);
                                EventHandler<DirEventsReplyEventArgs> DirEventsEventHandler =
                                    (sender, args) =>
                                    {
                                        handledEvents += args.MatchedEvents.Count;
                                        Parallel.ForEach(args.MatchedEvents, o =>
                                        {
                                            int score = !string.IsNullOrEmpty(fields)
                                                ? wasGetFields(searchEvent, searchEvent.GetType().Name)
                                                    .Sum(
                                                        p =>
                                                            (from q in
                                                                wasGetFields(o, o.GetType().Name)
                                                                let r = wasGetInfoValue(p.Key, p.Value)
                                                                where r != null
                                                                let s = wasGetInfoValue(q.Key, q.Value)
                                                                where s != null
                                                                where r.Equals(s)
                                                                select r).Count())
                                                : 0;
                                            lock (LockObject)
                                            {
                                                events.Add(o, score);
                                            }
                                        });
                                        if (((handledEvents - counter)%
                                             LINDEN_CONSTANTS.DIRECTORY.EVENT.SEARCH_RESULTS_COUNT).Equals(0))
                                        {
                                            ++counter;
                                            Client.Directory.StartEventsSearch(name, (uint) handledEvents);
                                        }
                                        DirEventsReplyEvent.Set();
                                    };
                                lock (ServicesLock)
                                {
                                    Client.Directory.DirEventsReply += DirEventsEventHandler;
                                    Client.Directory.StartEventsSearch(name,
                                        (uint) handledEvents);
                                    DirEventsReplyEvent.WaitOne(timeout, false);
                                    Client.Directory.DirEventsReply -= DirEventsEventHandler;
                                }
                                DirectoryManager.EventsSearchData topEvent =
                                    events.OrderByDescending(o => o.Value).FirstOrDefault().Key;
                                Parallel.ForEach(wasGetFields(topEvent, topEvent.GetType().Name),
                                    o =>
                                    {
                                        lock (LockObject)
                                        {
                                            csv.Add(o.Key.Name);
                                            csv.AddRange(wasGetInfo(o.Key, o.Value));
                                        }
                                    });
                                break;
                            case Type.GROUP:
                                if (string.IsNullOrEmpty(name))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_SEARCH_TEXT_PROVIDED));
                                }
                                DirectoryManager.GroupSearchData searchGroup = new DirectoryManager.GroupSearchData();
                                wasCSVToStructure(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA), message)),
                                    ref searchGroup);
                                Dictionary<DirectoryManager.GroupSearchData, int> groups =
                                    new Dictionary<DirectoryManager.GroupSearchData, int>();
                                ManualResetEvent DirGroupsReplyEvent = new ManualResetEvent(false);
                                EventHandler<DirGroupsReplyEventArgs> DirGroupsEventHandler =
                                    (sender, args) =>
                                    {
                                        handledEvents += args.MatchedGroups.Count;
                                        Parallel.ForEach(args.MatchedGroups, o =>
                                        {
                                            int score = !string.IsNullOrEmpty(fields)
                                                ? wasGetFields(searchGroup, searchGroup.GetType().Name)
                                                    .Sum(
                                                        p =>
                                                            (from q in
                                                                wasGetFields(o, o.GetType().Name)
                                                                let r = wasGetInfoValue(p.Key, p.Value)
                                                                where r != null
                                                                let s = wasGetInfoValue(q.Key, q.Value)
                                                                where s != null
                                                                where r.Equals(s)
                                                                select r).Count())
                                                : 0;
                                            lock (LockObject)
                                            {
                                                groups.Add(o, score);
                                            }
                                        });
                                        if (((handledEvents - counter)%
                                             LINDEN_CONSTANTS.DIRECTORY.GROUP.SEARCH_RESULTS_COUNT).Equals(0))
                                        {
                                            ++counter;
                                            Client.Directory.StartGroupSearch(name, handledEvents);
                                        }
                                        DirGroupsReplyEvent.Set();
                                    };
                                lock (ServicesLock)
                                {
                                    Client.Directory.DirGroupsReply += DirGroupsEventHandler;
                                    Client.Directory.StartGroupSearch(name, handledEvents);
                                    DirGroupsReplyEvent.WaitOne(timeout, false);
                                    Client.Directory.DirGroupsReply -= DirGroupsEventHandler;
                                }
                                DirectoryManager.GroupSearchData topGroup =
                                    groups.OrderByDescending(o => o.Value).FirstOrDefault().Key;
                                Parallel.ForEach(wasGetFields(topGroup, topGroup.GetType().Name),
                                    o =>
                                    {
                                        lock (LockObject)
                                        {
                                            csv.Add(o.Key.Name);
                                            csv.AddRange(wasGetInfo(o.Key, o.Value));
                                        }
                                    });
                                break;
                            case Type.LAND:
                                DirectoryManager.DirectoryParcel searchLand = new DirectoryManager.DirectoryParcel();
                                wasCSVToStructure(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA), message)),
                                    ref searchLand);
                                Dictionary<DirectoryManager.DirectoryParcel, int> lands =
                                    new Dictionary<DirectoryManager.DirectoryParcel, int>();
                                ManualResetEvent DirLandReplyEvent = new ManualResetEvent(false);
                                EventHandler<DirLandReplyEventArgs> DirLandReplyEventArgs =
                                    (sender, args) =>
                                    {
                                        handledEvents += args.DirParcels.Count;
                                        Parallel.ForEach(args.DirParcels, o =>
                                        {
                                            int score = !string.IsNullOrEmpty(fields)
                                                ? wasGetFields(searchLand, searchLand.GetType().Name)
                                                    .Sum(
                                                        p =>
                                                            (from q in
                                                                wasGetFields(o, o.GetType().Name)
                                                                let r = wasGetInfoValue(p.Key, p.Value)
                                                                where r != null
                                                                let s = wasGetInfoValue(q.Key, q.Value)
                                                                where s != null
                                                                where r.Equals(s)
                                                                select r).Count())
                                                : 0;
                                            lock (LockObject)
                                            {
                                                lands.Add(o, score);
                                            }
                                        });
                                        if (((handledEvents - counter)%
                                             LINDEN_CONSTANTS.DIRECTORY.LAND.SEARCH_RESULTS_COUNT).Equals(0))
                                        {
                                            ++counter;
                                            Client.Directory.StartLandSearch(DirectoryManager.DirFindFlags.SortAsc,
                                                DirectoryManager.SearchTypeFlags.Any, int.MaxValue, int.MaxValue,
                                                handledEvents);
                                        }
                                        DirLandReplyEvent.Set();
                                    };
                                lock (ServicesLock)
                                {
                                    Client.Directory.DirLandReply += DirLandReplyEventArgs;
                                    Client.Directory.StartLandSearch(DirectoryManager.DirFindFlags.SortAsc,
                                        DirectoryManager.SearchTypeFlags.Any, int.MaxValue, int.MaxValue, handledEvents);
                                    DirLandReplyEvent.WaitOne(timeout, false);
                                    Client.Directory.DirLandReply -= DirLandReplyEventArgs;
                                }
                                DirectoryManager.DirectoryParcel topLand =
                                    lands.OrderByDescending(o => o.Value).FirstOrDefault().Key;
                                Parallel.ForEach(wasGetFields(topLand, topLand.GetType().Name),
                                    o =>
                                    {
                                        lock (LockObject)
                                        {
                                            csv.Add(o.Key.Name);
                                            csv.AddRange(wasGetInfo(o.Key, o.Value));
                                        }
                                    });
                                break;
                            case Type.PEOPLE:
                                if (string.IsNullOrEmpty(name))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_SEARCH_TEXT_PROVIDED));
                                }
                                DirectoryManager.AgentSearchData searchAgent = new DirectoryManager.AgentSearchData();
                                Dictionary<DirectoryManager.AgentSearchData, int> agents =
                                    new Dictionary<DirectoryManager.AgentSearchData, int>();
                                ManualResetEvent AgentSearchDataEvent = new ManualResetEvent(false);
                                EventHandler<DirPeopleReplyEventArgs> DirPeopleReplyEventHandler =
                                    (sender, args) =>
                                    {
                                        handledEvents += args.MatchedPeople.Count;
                                        Parallel.ForEach(args.MatchedPeople, o =>
                                        {
                                            int score = !string.IsNullOrEmpty(fields)
                                                ? wasGetFields(searchAgent, searchAgent.GetType().Name)
                                                    .Sum(
                                                        p =>
                                                            (from q in
                                                                wasGetFields(o, o.GetType().Name)
                                                                let r = wasGetInfoValue(p.Key, p.Value)
                                                                where r != null
                                                                let s = wasGetInfoValue(q.Key, q.Value)
                                                                where s != null
                                                                where r.Equals(s)
                                                                select r).Count())
                                                : 0;
                                            lock (LockObject)
                                            {
                                                agents.Add(o, score);
                                            }
                                        });
                                        if (((handledEvents - counter)%
                                             LINDEN_CONSTANTS.DIRECTORY.PEOPLE.SEARCH_RESULTS_COUNT).Equals(0))
                                        {
                                            ++counter;
                                            Client.Directory.StartPeopleSearch(name, handledEvents);
                                        }
                                        AgentSearchDataEvent.Set();
                                    };
                                lock (ServicesLock)
                                {
                                    Client.Directory.DirPeopleReply += DirPeopleReplyEventHandler;
                                    Client.Directory.StartPeopleSearch(name, handledEvents);
                                    AgentSearchDataEvent.WaitOne(timeout, false);
                                    Client.Directory.DirPeopleReply -= DirPeopleReplyEventHandler;
                                }
                                DirectoryManager.AgentSearchData topAgent =
                                    agents.OrderByDescending(o => o.Value).FirstOrDefault().Key;
                                Parallel.ForEach(wasGetFields(topAgent, topAgent.GetType().Name),
                                    o =>
                                    {
                                        lock (LockObject)
                                        {
                                            csv.Add(o.Key.Name);
                                            csv.AddRange(wasGetInfo(o.Key, o.Value));
                                        }
                                    });
                                break;
                            case Type.PLACE:
                                if (string.IsNullOrEmpty(name))
                                {
                                    throw new Exception(
                                        wasGetDescriptionFromEnumValue(ScriptError.NO_SEARCH_TEXT_PROVIDED));
                                }
                                DirectoryManager.PlacesSearchData searchPlaces = new DirectoryManager.PlacesSearchData();
                                wasCSVToStructure(
                                    wasUriUnescapeDataString(
                                        wasKeyValueGet(wasGetDescriptionFromEnumValue(ScriptKeys.DATA), message)),
                                    ref searchPlaces);
                                Dictionary<DirectoryManager.PlacesSearchData, int> places =
                                    new Dictionary<DirectoryManager.PlacesSearchData, int>();
                                ManualResetEvent DirPlacesReplyEvent = new ManualResetEvent(false);
                                EventHandler<PlacesReplyEventArgs> DirPlacesReplyEventHandler =
                                    (sender, args) => Parallel.ForEach(args.MatchedPlaces, o =>
                                    {
                                        int score = !string.IsNullOrEmpty(fields)
                                            ? wasGetFields(searchPlaces, searchPlaces.GetType().Name)
                                                .Sum(
                                                    p =>
                                                        (from q in
                                                            wasGetFields(o, o.GetType().Name)
                                                            let r = wasGetInfoValue(p.Key, p.Value)
                                                            where r != null
                                                            let s = wasGetInfoValue(q.Key, q.Value)
                                                            where s != null
                                                            where r.Equals(s)
                                                            select r).Count())
                                            : 0;
                                        lock (LockObject)
                                        {
                                            places.Add(o, score);
                                        }
                                    });
                                lock (ServicesLock)
                                {
                                    Client.Directory.PlacesReply += DirPlacesReplyEventHandler;
                                    Client.Directory.StartPlacesSearch(name);
                                    DirPlacesReplyEvent.WaitOne(timeout, false);
                                    DirPlacesReplyEvent.Close();
                                    Client.Directory.PlacesReply -= DirPlacesReplyEventHandler;
                                }
                                DirectoryManager.PlacesSearchData topPlace =
                                    places.OrderByDescending(o => o.Value).FirstOrDefault().Key;
                                Parallel.ForEach(wasGetFields(topPlace, topPlace.GetType().Name),
                                    o =>
                                    {
                                        lock (LockObject)
                                        {
                                            csv.Add(o.Key.Name);
                                            csv.AddRange(wasGetInfo(o.Key, o.Value));
                                        }
                                    });
                                break;
                            default:
                                throw new Exception(
                                    wasGetDescriptionFromEnumValue(ScriptError.UNKNOWN_DIRECTORY_SEARCH_TYPE));
                        }
                        if (!csv.Count.Equals(0))
                        {
                            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                                string.Join(LINDEN_CONSTANTS.LSL.CSV_DELIMITER, csv.ToArray()));
                        }
                    };
                    break;
                default:
                    execute =
                        () => { throw new Exception(wasGetDescriptionFromEnumValue(ScriptError.COMMAND_NOT_FOUND)); };
                    break;
            }

            // execute command and check for errors
            bool success = false;
            try
            {
                execute.Invoke();
                success = true;
            }
            catch (Exception e)
            {
                result.Add(wasGetDescriptionFromEnumValue(ResultKeys.ERROR), e.Message);
            }
            // add the final success status
            result.Add(wasGetDescriptionFromEnumValue(ResultKeys.SUCCESS),
                success.ToString(CultureInfo.InvariantCulture));

            // build afterburn
            object AfterBurnLock = new object();
            HashSet<string> resultKeys = new HashSet<string>(wasGetEnumDescriptions<ResultKeys>());
            HashSet<string> scriptKeys = new HashSet<string>(wasGetEnumDescriptions<ScriptKeys>());
            Parallel.ForEach(wasKeyValueDecode(message), o =>
            {
                // remove keys that are script keys, result keys or invalid key-value pairs
                if (string.IsNullOrEmpty(o.Key) || resultKeys.Contains(o.Key) || scriptKeys.Contains(o.Key) ||
                    string.IsNullOrEmpty(o.Value))
                    return;
                lock (AfterBurnLock)
                {
                    result.Add(wasUriEscapeDataString(o.Key), wasUriEscapeDataString(o.Value));
                }
            });

            return result;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Gets the values from structures as strings.
        /// </summary>
        /// <typeparam name="T">the type of the structure</typeparam>
        /// <param name="structure">the structure</param>
        /// <param name="query">a CSV list of fields or properties to get</param>
        /// <returns>value strings</returns>
        private static IEnumerable<string> GetStructuredData<T>(T structure, string query)
        {
            HashSet<string[]> result = new HashSet<string[]>();
            object LockObject = new object();
            Parallel.ForEach(query.Split(new[] {LINDEN_CONSTANTS.LSL.CSV_DELIMITER},
                StringSplitOptions.RemoveEmptyEntries), name =>
                {
                    KeyValuePair<FieldInfo, object> fi = wasGetFields(structure,
                        structure.GetType().Name)
                        .FirstOrDefault(o => o.Key.Name.Equals(name, StringComparison.Ordinal));

                    lock (LockObject)
                    {
                        List<string> data = new List<string> {name};
                        data.AddRange(wasGetInfo(fi.Key, fi.Value));
                        if (data.Count >= 2)
                        {
                            result.Add(data.ToArray());
                        }
                    }

                    KeyValuePair<PropertyInfo, object> pi =
                        wasGetProperties(structure, structure.GetType().Name)
                            .FirstOrDefault(
                                o => o.Key.Name.Equals(name, StringComparison.Ordinal));
                    lock (LockObject)
                    {
                        List<string> data = new List<string> {name};
                        data.AddRange(wasGetInfo(pi.Key, pi.Value));
                        if (data.Count >= 2)
                        {
                            result.Add(data.ToArray());
                        }
                    }
                });
            return result.SelectMany(data => data);
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Takes as input a CSV data values and sets the corresponding
        ///     structure's fields or properties from the CSV data.
        /// </summary>
        /// <typeparam name="T">the type of the structure</typeparam>
        /// <param name="data">a CSV string</param>
        /// <param name="structure">the structure to set the fields and properties for</param>
        private static void wasCSVToStructure<T>(string data, ref T structure)
        {
            foreach (
                KeyValuePair<string, string> match in
                    Regex.Matches(data, @"\s*(?<key>.+?)\s*,\s*(?<value>.+?)\s*(,|$)").
                        Cast<Match>().
                        ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value))
            {
                KeyValuePair<string, string> localMatch = match;
                KeyValuePair<FieldInfo, object> fi =
                    wasGetFields(structure, structure.GetType().Name)
                        .FirstOrDefault(
                            o =>
                                o.Key.Name.Equals(localMatch.Key,
                                    StringComparison.Ordinal));

                wasSetInfo(fi.Key, fi.Value, match.Value, ref structure);

                KeyValuePair<PropertyInfo, object> pi =
                    wasGetProperties(structure, structure.GetType().Name)
                        .FirstOrDefault(
                            o =>
                                o.Key.Name.Equals(localMatch.Key,
                                    StringComparison.Ordinal));

                wasSetInfo(pi.Key, pi.Value, match.Value, ref structure);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Sends a post request to an URL with set key-value pairs.
        /// </summary>
        /// <param name="URL">the url to send the message to</param>
        /// <param name="message">key-value pairs to send</param>
        /// <param name="millisecondsTimeout">the time in milliseconds for the request to timeout</param>
        private static void wasPOST(string URL, Dictionary<string, string> message, int millisecondsTimeout)
        {
            HttpWebRequest request = (HttpWebRequest) WebRequest.Create(URL);
            request.Timeout = millisecondsTimeout;
            request.AllowAutoRedirect = true;
            request.AllowWriteStreamBuffering = true;
            request.Pipelined = true;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Method = WebRequestMethods.Http.Post;
            request.ContentType = "application/x-www-form-urlencoded";
            request.UserAgent = string.Format("{0}/{1} ({2})", "Corrade", CORRADE_VERSION, "http://was.fm/");
            byte[] byteArray =
                Encoding.UTF8.GetBytes(wasKeyValueEncode(message));
            request.ContentLength = byteArray.Length;
            using (Stream dataStream = request.GetRequestStream())
            {
                dataStream.Write(byteArray, 0, byteArray.Length);
                dataStream.Flush();
                dataStream.Close();
            }
        }

        private static void HandleTerseObjectUpdate(object sender, TerseObjectUpdateEventArgs e)
        {
            if (e.Prim.LocalID.Equals(Client.Self.LocalID))
            {
                SetDefaultCamera();
            }
            new Thread(o => SendNotification(Notifications.NOTIFICATION_TERSE_UPDATES, e)) {IsBackground = true}.Start();
        }

        private static void HandleAvatarUpdate(object sender, AvatarUpdateEventArgs e)
        {
            if (e.Avatar.LocalID.Equals(Client.Self.LocalID))
            {
                SetDefaultCamera();
            }
        }

        private static void HandleSimChanged(object sender, SimChangedEventArgs e)
        {
            Client.Self.Movement.SetFOVVerticalAngle(Utils.TWO_PI - 0.05f);
            new Thread(o => SendNotification(Notifications.NOTIFICATION_REGION_CROSSED, e)) {IsBackground = true}.Start();
        }

        private static void HandleMoneyBalance(object sender, MoneyBalanceReplyEventArgs e)
        {
            new Thread(o => SendNotification(Notifications.NOTIFICATION_ECONOMY, e)) {IsBackground = true}.Start();
        }

        private static void SetDefaultCamera()
        {
            // SetCamera 5m behind the avatar
            Client.Self.Movement.Camera.LookAt(
                Client.Self.SimPosition + new Vector3(-5, 0, 0)*Client.Self.Movement.BodyRotation,
                Client.Self.SimPosition
                );
        }

        #region NAME AND UUID RESOLVERS

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Updates the current balance by requesting it from the grid.
        /// </summary>
        /// <param name="millisecondsTimeout">timeout for the request in milliseconds</param>
        /// <returns>true if the balance could be retrieved</returns>
        private static bool lookupUpdateBalance(int millisecondsTimeout)
        {
            ManualResetEvent MoneyBalanceEvent = new ManualResetEvent(false);
            EventHandler<MoneyBalanceReplyEventArgs> MoneyBalanceEventHandler =
                (sender, args) => MoneyBalanceEvent.Set();
            Client.Self.MoneyBalanceReply += MoneyBalanceEventHandler;
            Client.Self.RequestBalance();
            if (!MoneyBalanceEvent.WaitOne(millisecondsTimeout, false))
            {
                Client.Self.MoneyBalanceReply -= MoneyBalanceEventHandler;
                return false;
            }
            Client.Self.MoneyBalanceReply -= MoneyBalanceEventHandler;
            return true;
        }

        /// <summary>
        ///     A wrapper for updating the money balance including locking.
        /// </summary>
        /// <param name="millisecondsTimeout">timeout for the request in milliseconds</param>
        /// <returns>true if the balance could be retrieved</returns>
        private static bool UpdateBalance(int millisecondsTimeout)
        {
            lock (ServicesLock)
            {
                return lookupUpdateBalance(millisecondsTimeout);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves a group name to an UUID by using the directory search.
        /// </summary>
        /// <param name="groupName">the name of the group to resolve</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="groupUUID">an object in which to store the UUID of the group</param>
        /// <returns>true if the group name could be resolved to an UUID</returns>
        private static bool lookupGroupNameToUUID(string groupName, int millisecondsTimeout, ref UUID groupUUID)
        {
            UUID localGroupUUID = UUID.Zero;
            ManualResetEvent DirGroupsEvent = new ManualResetEvent(false);
            EventHandler<DirGroupsReplyEventArgs> DirGroupsReplyDelegate = (sender, args) =>
            {
                localGroupUUID = args.MatchedGroups.FirstOrDefault(o => o.GroupName.Equals(groupName)).GroupID;
                if (!localGroupUUID.Equals(UUID.Zero))
                {
                    DirGroupsEvent.Set();
                }
            };
            Client.Directory.DirGroupsReply += DirGroupsReplyDelegate;
            Client.Directory.StartGroupSearch(groupName, 0);
            if (!DirGroupsEvent.WaitOne(millisecondsTimeout, false))
            {
                Client.Directory.DirGroupsReply -= DirGroupsReplyDelegate;
                return false;
            }
            Client.Directory.DirGroupsReply -= DirGroupsReplyDelegate;
            groupUUID = localGroupUUID;
            return true;
        }

        /// <summary>
        ///     A wrapper for resolving group names to UUIDs by using Corrade's internal cache.
        /// </summary>
        /// <param name="groupName">the name of the group to resolve</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="groupUUID">an object in which to store the UUID of the group</param>
        /// <returns>true if the group name could be resolved to an UUID</returns>
        private static bool GroupNameToUUID(string groupName, int millisecondsTimeout, ref UUID groupUUID)
        {
            lock (Cache.Locks.GroupCacheLock)
            {
                Cache.Groups group = Cache.GroupCache.FirstOrDefault(o => o.Name.Equals(groupName));

                if (!group.Equals(default(Cache.Groups)))
                {
                    groupUUID = group.UUID;
                    return true;
                }
            }
            bool succeeded;
            lock (ServicesLock)
            {
                succeeded = lookupGroupNameToUUID(groupName, millisecondsTimeout, ref groupUUID);
            }
            if (succeeded)
            {
                lock (Cache.Locks.GroupCacheLock)
                {
                    Cache.GroupCache.Add(new Cache.Groups
                    {
                        Name = groupName,
                        UUID = groupUUID
                    });
                }
            }
            return succeeded;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves an agent name to an agent UUID by searching the directory
        ///     services.
        /// </summary>
        /// <param name="agentFirstName">the first name of the agent</param>
        /// <param name="agentLastName">the last name of the agent</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="agentUUID">an object to store the agent UUID</param>
        /// <returns>true if the agent name could be resolved to an UUID</returns>
        private static bool lookupAgentNameToUUID(string agentFirstName, string agentLastName, int millisecondsTimeout,
            ref UUID agentUUID)
        {
            UUID localAgentUUID = UUID.Zero;
            ManualResetEvent agentUUIDEvent = new ManualResetEvent(false);
            EventHandler<DirPeopleReplyEventArgs> DirPeopleReplyDelegate = (sender, args) =>
            {
                localAgentUUID =
                    args.MatchedPeople.FirstOrDefault(
                        o =>
                            o.FirstName.Equals(agentFirstName, StringComparison.OrdinalIgnoreCase) &&
                            o.LastName.Equals(agentLastName, StringComparison.OrdinalIgnoreCase)).AgentID;
                if (!localAgentUUID.Equals(UUID.Zero))
                {
                    agentUUIDEvent.Set();
                }
            };
            Client.Directory.DirPeopleReply += DirPeopleReplyDelegate;
            Client.Directory.StartPeopleSearch(
                string.Format(CultureInfo.InvariantCulture, "{0} {1}", agentFirstName, agentLastName), 0);
            if (!agentUUIDEvent.WaitOne(millisecondsTimeout, false))
            {
                Client.Directory.DirPeopleReply -= DirPeopleReplyDelegate;
                return false;
            }
            Client.Directory.DirPeopleReply -= DirPeopleReplyDelegate;
            agentUUID = localAgentUUID;
            return true;
        }

        /// <summary>
        ///     A wrapper for looking up an agent name using Corrade's internal cache.
        /// </summary>
        /// <param name="agentFirstName">the first name of the agent</param>
        /// <param name="agentLastName">the last name of the agent</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="agentUUID">an object to store the agent UUID</param>
        /// <returns>true if the agent name could be resolved to an UUID</returns>
        private static bool AgentNameToUUID(string agentFirstName, string agentLastName, int millisecondsTimeout,
            ref UUID agentUUID)
        {
            lock (Cache.Locks.AgentCacheLock)
            {
                Cache.Agents agent =
                    Cache.AgentCache.FirstOrDefault(
                        o => o.FirstName.Equals(agentFirstName) && o.LastName.Equals(agentLastName));

                if (!agent.Equals(default(Cache.Agents)))
                {
                    agentUUID = agent.UUID;
                    return true;
                }
            }
            bool succeeded;
            lock (ServicesLock)
            {
                succeeded = lookupAgentNameToUUID(agentFirstName, agentLastName, millisecondsTimeout, ref agentUUID);
            }
            if (succeeded)
            {
                lock (Cache.Locks.AgentCacheLock)
                {
                    Cache.AgentCache.Add(new Cache.Agents
                    {
                        FirstName = agentFirstName,
                        LastName = agentLastName,
                        UUID = agentUUID
                    });
                }
            }
            return succeeded;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves an agent UUID to an agent name.
        /// </summary>
        /// <param name="agentUUID">the UUID of the agent</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="agentName">an object to store the name of the agent in</param>
        /// <returns>true if the UUID could be resolved to a name</returns>
        private static bool lookupAgentUUIDToName(UUID agentUUID, int millisecondsTimeout, ref string agentName)
        {
            if (agentUUID.Equals(UUID.Zero))
                return false;
            string localAgentName = string.Empty;
            ManualResetEvent agentNameEvent = new ManualResetEvent(false);
            EventHandler<UUIDNameReplyEventArgs> UUIDNameReplyDelegate = (sender, args) =>
            {
                localAgentName = args.Names.FirstOrDefault(o => o.Key.Equals(agentUUID)).Value;
                if (!string.IsNullOrEmpty(localAgentName))
                {
                    agentNameEvent.Set();
                }
            };
            Client.Avatars.UUIDNameReply += UUIDNameReplyDelegate;
            Client.Avatars.RequestAvatarName(agentUUID);
            if (!agentNameEvent.WaitOne(millisecondsTimeout, false))
            {
                Client.Avatars.UUIDNameReply -= UUIDNameReplyDelegate;
                return false;
            }
            Client.Avatars.UUIDNameReply -= UUIDNameReplyDelegate;
            agentName = localAgentName;
            return true;
        }

        /// <summary>
        ///     A wrapper for agent to UUID lookups using Corrade's internal cache.
        /// </summary>
        /// <param name="agentUUID">the UUID of the agent</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="agentName">an object to store the name of the agent in</param>
        /// <returns>true if the UUID could be resolved to a name</returns>
        private static bool AgentUUIDToName(UUID agentUUID, int millisecondsTimeout, ref string agentName)
        {
            lock (Cache.Locks.AgentCacheLock)
            {
                Cache.Agents agent = Cache.AgentCache.FirstOrDefault(o => o.UUID.Equals(agentUUID));

                if (!agent.Equals(default(Cache.Agents)))
                {
                    agentName = string.Join(" ", new[] {agent.FirstName, agent.LastName});
                    return true;
                }
            }
            bool succeeded;
            lock (ServicesLock)
            {
                succeeded = lookupAgentUUIDToName(agentUUID, millisecondsTimeout, ref agentName);
            }
            if (succeeded)
            {
                List<string> name = new List<string>(GetAvatarNames(agentName));
                lock (Cache.Locks.AgentCacheLock)
                {
                    Cache.AgentCache.Add(new Cache.Agents
                    {
                        FirstName = name.First(),
                        LastName = name.Last(),
                        UUID = agentUUID
                    });
                }
            }
            return succeeded;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// ///
        /// <summary>
        ///     Resolves a role name to a role UUID.
        /// </summary>
        /// <param name="roleName">the name of the role to be resolved to an UUID</param>
        /// <param name="groupUUID">the UUID of the group to query for the role UUID</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="roleUUID">an UUID object to store the role UUID in</param>
        /// <returns>true if the role could be found</returns>
        private static bool lookupRoleNameToRoleUUID(string roleName, UUID groupUUID, int millisecondsTimeout,
            ref UUID roleUUID)
        {
            UUID localRoleUUID = UUID.Zero;
            ManualResetEvent GroupRoleDataEvent = new ManualResetEvent(false);
            EventHandler<GroupRolesDataReplyEventArgs> GroupRoleDataReplyDelegate = (sender, args) =>
            {
                localRoleUUID =
                    args.Roles.FirstOrDefault(o => o.Value.Name.Equals(roleName, StringComparison.Ordinal))
                        .Key;
                if (!localRoleUUID.Equals(UUID.Zero))
                {
                    GroupRoleDataEvent.Set();
                }
            };
            Client.Groups.GroupRoleDataReply += GroupRoleDataReplyDelegate;
            Client.Groups.RequestGroupRoles(groupUUID);
            if (!GroupRoleDataEvent.WaitOne(millisecondsTimeout, false))
            {
                Client.Groups.GroupRoleDataReply -= GroupRoleDataReplyDelegate;
                return false;
            }
            Client.Groups.GroupRoleDataReply -= GroupRoleDataReplyDelegate;
            roleUUID = localRoleUUID;
            return true;
        }

        /// <summary>
        ///     A wrapper to resolve role names to role UUIDs - used for locking service requests.
        /// </summary>
        /// <param name="roleName">the name of the role to be resolved to an UUID</param>
        /// <param name="groupUUID">the UUID of the group to query for the role UUID</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="roleUUID">an UUID object to store the role UUID in</param>
        /// <returns>true if the role could be found</returns>
        private static bool RoleNameToRoleUUID(string roleName, UUID groupUUID, int millisecondsTimeout,
            ref UUID roleUUID)
        {
            lock (ServicesLock)
            {
                return lookupRoleNameToRoleUUID(roleName, groupUUID, millisecondsTimeout, ref roleUUID);
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves a role name to a role UUID.
        /// </summary>
        /// <param name="RoleUUID">the UUID of the role to be resolved to a name</param>
        /// <param name="GroupUUID">the UUID of the group to query for the role name</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="roleName">a string object to store the role name in</param>
        /// <returns>true if the role could be resolved</returns>
        private static bool lookupRoleUUIDToName(UUID RoleUUID, UUID GroupUUID, int millisecondsTimeout,
            ref string roleName)
        {
            if (RoleUUID.Equals(UUID.Zero) || GroupUUID.Equals(UUID.Zero))
                return false;
            string localRoleName = string.Empty;
            ManualResetEvent GroupRoleDataEvent = new ManualResetEvent(false);
            EventHandler<GroupRolesDataReplyEventArgs> GroupRoleDataReplyDelegate = (sender, args) =>
            {
                localRoleName = args.Roles.FirstOrDefault(o => o.Key.Equals(RoleUUID)).Value.Name;
                if (!string.IsNullOrEmpty(localRoleName))
                {
                    GroupRoleDataEvent.Set();
                }
            };

            Client.Groups.GroupRoleDataReply += GroupRoleDataReplyDelegate;
            Client.Groups.RequestGroupRoles(GroupUUID);
            if (!GroupRoleDataEvent.WaitOne(millisecondsTimeout, false))
            {
                Client.Groups.GroupRoleDataReply -= GroupRoleDataReplyDelegate;
                return false;
            }
            Client.Groups.GroupRoleDataReply -= GroupRoleDataReplyDelegate;
            roleName = localRoleName;
            return true;
        }

        /// <summary>
        ///     Wrapper for resolving role UUIDs to names - used for locking service requests.
        /// </summary>
        /// <param name="RoleUUID"></param>
        /// <param name="GroupUUID"></param>
        /// <param name="millisecondsTimeout"></param>
        /// <param name="roleName"></param>
        /// <returns></returns>
        private static bool RoleUUIDToName(UUID RoleUUID, UUID GroupUUID, int millisecondsTimeout, ref string roleName)
        {
            lock (ServicesLock)
            {
                return lookupRoleUUIDToName(RoleUUID, GroupUUID, millisecondsTimeout, ref roleName);
            }
        }

        #endregion

        #region KEY-VALUE DATA

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns the value of a key from a key-value data string.
        /// </summary>
        /// <param name="key">the key of the value</param>
        /// <param name="data">the key-value data segment</param>
        /// <returns>true if the key was found in data</returns>
        private static string wasKeyValueGet(string key, string data)
        {
            foreach (string tuples in data.Split('&'))
            {
                string[] tuple = tuples.Split('=');
                if (!tuple.Length.Equals(2))
                {
                    continue;
                }
                if (tuple[0].Equals(key, StringComparison.Ordinal))
                {
                    return tuple[1];
                }
            }
            return string.Empty;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns a key-value data string with a key set to a given value.
        /// </summary>
        /// <param name="key">the key of the value</param>
        /// <param name="value">the value to set the key to</param>
        /// <param name="data">the key-value data segment</param>
        /// <returns>
        ///     a key-value data string or the empty string if either key or
        ///     value are empty
        /// </returns>
        private static string wasKeyValueSet(string key, string value, string data)
        {
            List<string> output = new List<string>();
            foreach (string tuples in data.Split('&'))
            {
                string[] tuple = tuples.Split('=');
                if (!tuple.Length.Equals(2))
                {
                    continue;
                }
                if (tuple[0].Equals(key, StringComparison.Ordinal))
                {
                    tuple[1] = value;
                }
                output.Add(string.Join("=", tuple));
            }
            string add = string.Join("=", new[] {key, value});
            if (!output.Contains(add))
            {
                output.Add(add);
            }
            return string.Join("&", output.ToArray());
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Deletes a key-value pair from a string referenced by a key.
        /// </summary>
        /// <param name="key">the key to search for</param>
        /// <param name="data">the key-value data segment</param>
        /// <returns>a key-value pair string</returns>
        private static string wasKeyValueDelete(string key, string data)
        {
            List<string> output = new List<string>();
            foreach (string tuples in data.Split('&'))
            {
                string[] tuple = tuples.Split('=');
                if (!tuple.Length.Equals(2))
                {
                    continue;
                }
                if (tuple[0].Equals(key, StringComparison.Ordinal))
                {
                    continue;
                }
                output.Add(string.Join("=", tuple));
            }
            return string.Join("&", output.ToArray());
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Decodes key-value pair data to a dictionary.
        /// </summary>
        /// <param name="data">the key-value pair data</param>
        /// <returns>a dictionary containing the keys and values</returns>
        private static Dictionary<string, string> wasKeyValueDecode(string data)
        {
            Dictionary<string, string> output = new Dictionary<string, string>();
            foreach (string tuples in data.Split('&'))
            {
                string[] tuple = tuples.Split('=');
                if (!tuple.Length.Equals(2))
                {
                    continue;
                }
                if (output.ContainsKey(tuple[0]))
                {
                    continue;
                }
                output.Add(tuple[0], tuple[1]);
            }
            return output;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Serialises a dictionary to key-value data.
        /// </summary>
        /// <param name="data">a dictionary</param>
        /// <returns>a key-value data encoded string</returns>
        private static string wasKeyValueEncode(Dictionary<string, string> data)
        {
            List<string> output = new List<string>();
            foreach (KeyValuePair<string, string> tuple in data)
            {
                output.Add(string.Join("=", new[] {tuple.Key, tuple.Value}));
            }
            return string.Join("&", output.ToArray());
        }

        ///////////////////////////////////////////////////////////////////////////
        //  Copyright (C) Wizardry and Steamworks 2014 - License: GNU GPLv3      //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>URI unescapes an RFC3986 URI escaped string</summary>
        /// <param name="data">a string to unescape</param>
        /// <returns>the resulting string</returns>
        private static string wasUriUnescapeDataString(string data)
        {
            return
                Regex.Matches(data, @"%([0-9A-Fa-f]+?){2}")
                    .Cast<Match>()
                    .Select(m => m.Value)
                    .Distinct()
                    .Aggregate(data,
                        (current, match) =>
                            current.Replace(match,
                                ((char)
                                    int.Parse(match.Substring(1), NumberStyles.AllowHexSpecifier,
                                        CultureInfo.InvariantCulture)).ToString(
                                            CultureInfo.InvariantCulture)));
        }

        ///////////////////////////////////////////////////////////////////////////
        //  Copyright (C) Wizardry and Steamworks 2014 - License: GNU GPLv3      //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>RFC3986 URI Escapes a string</summary>
        /// <param name="data">a string to escape</param>
        /// <returns>an RFC3986 escaped string</returns>
        private static string wasUriEscapeDataString(string data)
        {
            StringBuilder result = new StringBuilder();
            foreach (char c in data.Select(o => o))
            {
                if (char.IsLetter(c) || char.IsDigit(c))
                {
                    result.Append(c);
                    continue;
                }
                result.AppendFormat("%{0:X2}", (int) c);
            }
            return result.ToString();
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2014 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>Escapes a dictionary's keys and values for sending as POST data.</summary>
        /// <param name="data">A dictionary containing keys and values to be escaped</param>
        private static Dictionary<string, string> wasKeyValueEscape(Dictionary<string, string> data)
        {
            Dictionary<string, string> output = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> tuple in data)
            {
                output.Add(wasUriEscapeDataString(tuple.Key), wasUriEscapeDataString(tuple.Value));
            }
            return output;
        }

        #endregion

        /// <summary>
        ///     Agent structure.
        /// </summary>
        private struct Agent
        {
            [Description("firstname")] public string FirstName;
            [Description("lastname")] public string LastName;
            [Description("uuid")] public UUID UUID;
        }

        /// <summary>
        ///     Constants used by Corrade.
        /// </summary>
        private struct CORRADE_CONSTANTS
        {
            /// <summary>
            ///     Copyright.
            /// </summary>
            public const string COPYRIGHT = @"(c) Copyright 2013 Wizardry and Steamworks";

            /// <summary>
            ///     Censor characters for passwords.
            /// </summary>
            public const string PASSWORD_CENSOR = "***";

            /// <summary>
            ///     Corrade channel sent to the simulator.
            /// </summary>
            public const string CLIENT_CHANNEL = @"[Wizardry and Steamworks]:Corrade";

            public const string CURRENT_OUTFIT_FOLDER_NAME = @"Current Outfit";
            public const string DEFAULT_SERVICE_NAME = @"Corrade";
            public const string LOG_FACILITY = @"Application";
            public const string WEB_REQUEST = @"Web Request";
            public const string TEXT_HTML = @"text/html";
            public const string CONFIGURATION_FILE = @"Corrade.ini";
            public const string DATE_TIME_STAMP = @"dd-MM-yyyy HH:mm";
            public const string INVENTORY_CACHE_FILE = @"Inventory.cache";
            public const string AGENT_CACHE_FILE = @"Agent.cache";
            public const string GROUP_CACHE_FILE = @"Group.cache";
            public const string PATH_SEPARATOR = @"/";
            public const string ERROR_SEPARATOR = @" : ";
            public const string CACHE_DIRECTORY = @"cache";

            public struct HTTP_CODES
            {
                public const int OK = 200;
            }
        }

        /// <summary>
        ///     Corrade's caches.
        /// </summary>
        public struct Cache
        {
            public static HashSet<Agents> AgentCache = new HashSet<Agents>();
            public static HashSet<Groups> GroupCache = new HashSet<Groups>();

            internal static void Purge()
            {
                lock (Locks.AgentCacheLock)
                {
                    AgentCache.Clear();
                }
                lock (Locks.GroupCacheLock)
                {
                    GroupCache.Clear();
                }
            }

            /// <summary>
            ///     Serializes to a file.
            /// </summary>
            /// <param name="FileName">File path of the new xml file</param>
            /// <param name="o">the object to save</param>
            public static void Save<T>(string FileName, T o)
            {
                try
                {
                    using (StreamWriter writer = new StreamWriter(FileName))
                    {
                        XmlSerializer serializer = new XmlSerializer(typeof (T));
                        serializer.Serialize(writer, o);
                        writer.Flush();
                    }
                }
                catch (Exception e)
                {
                    Feedback(wasGetDescriptionFromEnumValue(ConsoleError.UNABLE_TO_SAVE_CORRADE_CACHE), e.Message);
                }
            }

            /// <summary>
            ///     Load an object from an xml file
            /// </summary>
            /// <param name="FileName">Xml file name</param>
            /// <param name="o">the object to load to</param>
            /// <returns>The object created from the xml file</returns>
            public static T Load<T>(string FileName, T o)
            {
                if (!File.Exists(FileName)) return o;
                try
                {
                    using (FileStream stream = File.OpenRead(FileName))
                    {
                        XmlSerializer serializer = new XmlSerializer(typeof (T));
                        return (T) serializer.Deserialize(stream);
                    }
                }
                catch (Exception e)
                {
                    Feedback(wasGetDescriptionFromEnumValue(ConsoleError.UNABLE_TO_LOAD_CORRADE_CACHE), e.Message);
                }
                return o;
            }

            public struct Agents
            {
                public string FirstName;
                public string LastName;
                public UUID UUID;
            }

            public struct Groups
            {
                public string Name;
                public UUID UUID;
            }

            public struct Locks
            {
                public static readonly object AgentCacheLock = new object();
                public static readonly object GroupCacheLock = new object();
            }
        }

        /// <summary>
        ///     An element from the callback queue waiting to be dispatched.
        /// </summary>
        private struct CallbackQueueElement
        {
            public string URL;
            public Dictionary<string, string> message;
        }

        private struct Configuration
        {
            public static string FIRST_NAME;
            public static string LAST_NAME;
            public static string PASSWORD;
            public static string LOGIN_URL;
            public static bool ENABLE_HTTP_SERVER;
            public static string HTTP_SERVER_PREFIX;
            public static int HTTP_SERVER_TIMEOUT;
            public static int CALLBACK_TIMEOUT;
            public static int CALLBACK_THROTTLE;
            public static int CALLBACK_QUEUE_LENGTH;
            public static int NOTIFICATION_TIMEOUT;
            public static int NOTIFICATION_THROTTLE;
            public static int NOTIFICATION_QUEUE_LENGTH;
            public static int CONNECTION_LIMIT;
            public static bool USE_NAGGLE;
            public static bool USE_EXPECT100CONTINUE;
            public static int SERVICES_TIMEOUT;
            public static int REBAKE_DELAY;
            public static int MEMBERSHIP_SWEEP_INTERVAL;
            public static bool TOS_ACCEPTED;
            public static string START_LOCATION;
            public static string NETWORK_CARD_MAC;
            public static string LOG_FILE;
            public static bool AUTO_ACTIVATE_GROUP;
            public static int ACTIVATE_DELAY;
            public static int GROUP_CREATE_FEE;
            public static HashSet<Group> GROUPS;
            public static HashSet<Master> MASTERS;

            public static string Read(string file)
            {
                lock (ConfigurationFileLock)
                {
                    return File.ReadAllText(file);
                }
            }

            public static void Write(string file, string data)
            {
                lock (ConfigurationFileLock)
                {
                    File.WriteAllText(file, data);
                }
            }

            public static void Load(string file)
            {
                FIRST_NAME = string.Empty;
                LAST_NAME = string.Empty;
                PASSWORD = string.Empty;
                LOGIN_URL = string.Empty;
                ENABLE_HTTP_SERVER = false;
                HTTP_SERVER_PREFIX = "http://+:8080/";
                HTTP_SERVER_TIMEOUT = 5000;
                CALLBACK_TIMEOUT = 5000;
                CALLBACK_THROTTLE = 1000;
                CALLBACK_QUEUE_LENGTH = 100;
                NOTIFICATION_TIMEOUT = 5000;
                NOTIFICATION_THROTTLE = 1000;
                NOTIFICATION_QUEUE_LENGTH = 100;
                CONNECTION_LIMIT = 100;
                USE_NAGGLE = true;
                SERVICES_TIMEOUT = 60000;
                REBAKE_DELAY = 1000;
                ACTIVATE_DELAY = 5000;
                MEMBERSHIP_SWEEP_INTERVAL = 1000;
                TOS_ACCEPTED = false;
                START_LOCATION = "last";
                NETWORK_CARD_MAC = string.Empty;
                LOG_FILE = "Corrade.log";
                AUTO_ACTIVATE_GROUP = false;
                GROUP_CREATE_FEE = 100;
                GROUPS = new HashSet<Group>();
                MASTERS = new HashSet<Master>();

                try
                {
                    lock (ConfigurationFileLock)
                    {
                        file = File.ReadAllText(file);
                    }
                }
                catch (Exception e)
                {
                    Feedback(wasGetDescriptionFromEnumValue(ConsoleError.INVALID_CONFIGURATION_FILE), e.Message);
                    Environment.Exit(1);
                }

                XmlDocument conf = new XmlDocument();
                try
                {
                    conf.LoadXml(file);
                }
                catch (XmlException e)
                {
                    Feedback(wasGetDescriptionFromEnumValue(ConsoleError.INVALID_CONFIGURATION_FILE), e.Message);
                    Environment.Exit(1);
                }

                XmlNode root = conf.DocumentElement;
                if (root == null)
                {
                    Feedback(wasGetDescriptionFromEnumValue(ConsoleError.INVALID_CONFIGURATION_FILE));
                    Environment.Exit(1);
                }

                if (root != null)
                {
                    XmlNodeList nodeList = root.SelectNodes("/config/client/*");
                    if (nodeList == null)
                        return;
                    try
                    {
                        foreach (XmlNode client in nodeList)
                            switch (client.Name.ToLowerInvariant())
                            {
                                case ConfigurationKeys.FIRST_NAME:
                                    if (string.IsNullOrEmpty(client.InnerText))
                                    {
                                        throw new Exception("error in client section");
                                    }
                                    FIRST_NAME = client.InnerText;
                                    break;
                                case ConfigurationKeys.LAST_NAME:
                                    if (string.IsNullOrEmpty(client.InnerText))
                                    {
                                        throw new Exception("error in client section");
                                    }
                                    LAST_NAME = client.InnerText;
                                    break;
                                case ConfigurationKeys.PASSWORD:
                                    if (string.IsNullOrEmpty(client.InnerText))
                                    {
                                        throw new Exception("error in client section");
                                    }
                                    PASSWORD = client.InnerText;
                                    break;
                                case ConfigurationKeys.LOGIN_URL:
                                    if (string.IsNullOrEmpty(client.InnerText))
                                    {
                                        throw new Exception("error in client section");
                                    }
                                    LOGIN_URL = client.InnerText;
                                    break;
                                case ConfigurationKeys.TOS_ACCEPTED:
                                    if (!bool.TryParse(client.InnerText, out TOS_ACCEPTED))
                                    {
                                        throw new Exception("error in client section");
                                    }
                                    break;
                                case ConfigurationKeys.GROUP_CREATE_FEE:
                                    if (!int.TryParse(client.InnerText, out GROUP_CREATE_FEE))
                                    {
                                        throw new Exception("err r in client section");
                                    }
                                    break;
                                case ConfigurationKeys.AUTO_ACTIVATE_GROUP:
                                    if (!bool.TryParse(client.InnerText, out AUTO_ACTIVATE_GROUP))
                                    {
                                        throw new Exception("error in client section");
                                    }
                                    break;
                                case ConfigurationKeys.START_LOCATION:
                                    if (string.IsNullOrEmpty(client.InnerText))
                                    {
                                        throw new Exception("error in client section");
                                    }
                                    START_LOCATION = client.InnerText;
                                    break;
                                case ConfigurationKeys.LOG:
                                    if (string.IsNullOrEmpty(client.InnerText))
                                    {
                                        throw new Exception("error in client section");
                                    }
                                    LOG_FILE = client.InnerText;
                                    break;
                            }
                    }
                    catch (Exception e)
                    {
                        Feedback(wasGetDescriptionFromEnumValue(ConsoleError.INVALID_CONFIGURATION_FILE), e.Message);
                    }

                    // Process RLV.
                    nodeList = root.SelectNodes("/rlv/*");
                    if (nodeList != null)
                    {
                        try
                        {
                            foreach (XmlNode RLVNode in nodeList)
                            {
                                switch (RLVNode.Name.ToLowerInvariant())
                                {
                                    case ConfigurationKeys.ENABLE:
                                        bool enable;
                                        if (!bool.TryParse(RLVNode.InnerText, out enable))
                                        {
                                            throw new Exception("error in RLV section");
                                        }
                                        EnableCorradeRLV = enable;
                                        break;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.INVALID_CONFIGURATION_FILE), e.Message);
                        }
                    }

                    // Process server.
                    nodeList = root.SelectNodes("/config/server/*");
                    if (nodeList != null)
                    {
                        try
                        {
                            foreach (XmlNode serverNode in nodeList)
                            {
                                switch (serverNode.Name.ToLowerInvariant())
                                {
                                    case ConfigurationKeys.HTTP:
                                        if (!bool.TryParse(serverNode.InnerText, out ENABLE_HTTP_SERVER))
                                        {
                                            throw new Exception("error in server section");
                                        }
                                        break;
                                    case ConfigurationKeys.PREFIX:
                                        if (string.IsNullOrEmpty(serverNode.InnerText))
                                        {
                                            throw new Exception("error in server section");
                                        }
                                        HTTP_SERVER_PREFIX = serverNode.InnerText;
                                        break;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.INVALID_CONFIGURATION_FILE), e.Message);
                        }
                    }

                    // Process network.
                    nodeList = root.SelectNodes("/config/network/*");
                    if (nodeList != null)
                    {
                        try
                        {
                            foreach (XmlNode networkNode in nodeList)
                            {
                                switch (networkNode.Name.ToLowerInvariant())
                                {
                                    case ConfigurationKeys.MAC:
                                        if (!string.IsNullOrEmpty(networkNode.InnerText))
                                        {
                                            NETWORK_CARD_MAC = networkNode.InnerText;
                                        }
                                        break;
                                    case ConfigurationKeys.NAGGLE:
                                        if (!bool.TryParse(networkNode.InnerText, out USE_NAGGLE))
                                        {
                                            throw new Exception("error in network section");
                                        }
                                        break;
                                    case ConfigurationKeys.EXPECT100CONTINUE:
                                        if (!bool.TryParse(networkNode.InnerText, out USE_EXPECT100CONTINUE))
                                        {
                                            throw new Exception("error in network section");
                                        }
                                        break;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.INVALID_CONFIGURATION_FILE), e.Message);
                        }
                    }

                    // Process limits.
                    nodeList = root.SelectNodes("/config/limits/*");
                    if (nodeList != null)
                    {
                        try
                        {
                            foreach (XmlNode limitsNode in nodeList)
                            {
                                switch (limitsNode.Name.ToLowerInvariant())
                                {
                                    case ConfigurationKeys.CLIENT:
                                        XmlNodeList clientLimitNodeList = limitsNode.SelectNodes("*");
                                        if (clientLimitNodeList == null)
                                        {
                                            throw new Exception("error in client limits section");
                                        }
                                        foreach (XmlNode clientLimitNode in clientLimitNodeList)
                                        {
                                            switch (clientLimitNode.Name.ToLowerInvariant())
                                            {
                                                case ConfigurationKeys.CONNECTIONS:
                                                    if (
                                                        !int.TryParse(clientLimitNode.InnerText,
                                                            out CONNECTION_LIMIT))
                                                    {
                                                        throw new Exception("error in client limits section");
                                                    }
                                                    break;
                                            }
                                        }
                                        break;
                                    case ConfigurationKeys.CALLBACKS:
                                        XmlNodeList callbackLimitNodeList = limitsNode.SelectNodes("*");
                                        if (callbackLimitNodeList == null)
                                        {
                                            throw new Exception("error in callback limits section");
                                        }
                                        foreach (XmlNode callbackLimitNode in callbackLimitNodeList)
                                        {
                                            switch (callbackLimitNode.Name.ToLowerInvariant())
                                            {
                                                case ConfigurationKeys.TIMEOUT:
                                                    if (!int.TryParse(callbackLimitNode.InnerText, out CALLBACK_TIMEOUT))
                                                    {
                                                        throw new Exception("error in callback limits section");
                                                    }
                                                    break;
                                                case ConfigurationKeys.THROTTLE:
                                                    if (
                                                        !int.TryParse(callbackLimitNode.InnerText, out CALLBACK_THROTTLE))
                                                    {
                                                        throw new Exception("error in callback limits section");
                                                    }
                                                    break;
                                                case ConfigurationKeys.QUEUE_LENGTH:
                                                    if (
                                                        !int.TryParse(callbackLimitNode.InnerText,
                                                            out CALLBACK_QUEUE_LENGTH))
                                                    {
                                                        throw new Exception("error in callback limits section");
                                                    }
                                                    break;
                                            }
                                        }
                                        break;
                                    case ConfigurationKeys.NOTIFICATIONS:
                                        XmlNodeList notificationLimitNodeList = limitsNode.SelectNodes("*");
                                        if (notificationLimitNodeList == null)
                                        {
                                            throw new Exception("error in notification limits section");
                                        }
                                        foreach (XmlNode notificationLimitNode in notificationLimitNodeList)
                                        {
                                            switch (notificationLimitNode.Name.ToLowerInvariant())
                                            {
                                                case ConfigurationKeys.TIMEOUT:
                                                    if (
                                                        !int.TryParse(notificationLimitNode.InnerText,
                                                            out NOTIFICATION_TIMEOUT))
                                                    {
                                                        throw new Exception("error in notification limits section");
                                                    }
                                                    break;
                                                case ConfigurationKeys.THROTTLE:
                                                    if (
                                                        !int.TryParse(notificationLimitNode.InnerText,
                                                            out NOTIFICATION_THROTTLE))
                                                    {
                                                        throw new Exception("error in notification limits section");
                                                    }
                                                    break;
                                                case ConfigurationKeys.QUEUE_LENGTH:
                                                    if (
                                                        !int.TryParse(notificationLimitNode.InnerText,
                                                            out NOTIFICATION_QUEUE_LENGTH))
                                                    {
                                                        throw new Exception("error in callback limits section");
                                                    }
                                                    break;
                                            }
                                        }
                                        break;
                                    case ConfigurationKeys.SERVER:
                                        XmlNodeList HTTPServerLimitNodeList = limitsNode.SelectNodes("*");
                                        if (HTTPServerLimitNodeList == null)
                                        {
                                            throw new Exception("error in server limits section");
                                        }
                                        foreach (XmlNode HTTPServerLimitNode in HTTPServerLimitNodeList)
                                        {
                                            switch (HTTPServerLimitNode.Name.ToLowerInvariant())
                                            {
                                                case ConfigurationKeys.TIMEOUT:
                                                    if (
                                                        !int.TryParse(HTTPServerLimitNode.InnerText,
                                                            out HTTP_SERVER_TIMEOUT))
                                                    {
                                                        throw new Exception("error in server limits section");
                                                    }
                                                    break;
                                            }
                                        }
                                        break;
                                    case ConfigurationKeys.SERVICES:
                                        XmlNodeList servicesLimitNodeList = limitsNode.SelectNodes("*");
                                        if (servicesLimitNodeList == null)
                                        {
                                            throw new Exception("error in services limits section");
                                        }
                                        foreach (XmlNode servicesLimitNode in servicesLimitNodeList)
                                        {
                                            switch (servicesLimitNode.Name.ToLowerInvariant())
                                            {
                                                case ConfigurationKeys.TIMEOUT:
                                                    if (
                                                        !int.TryParse(servicesLimitNode.InnerText,
                                                            out SERVICES_TIMEOUT))
                                                    {
                                                        throw new Exception("error in services limits section");
                                                    }
                                                    break;

                                                case ConfigurationKeys.REBAKE:
                                                    if (!int.TryParse(servicesLimitNode.InnerText, out REBAKE_DELAY))
                                                    {
                                                        throw new Exception("error in services limits section");
                                                    }
                                                    break;
                                                case ConfigurationKeys.ACTIVATE:
                                                    if (
                                                        !int.TryParse(servicesLimitNode.InnerText,
                                                            out ACTIVATE_DELAY))
                                                    {
                                                        throw new Exception("error in services limits section");
                                                    }
                                                    break;
                                            }
                                        }
                                        break;
                                    case ConfigurationKeys.MEMBERSHIP:
                                        XmlNodeList membershipLimitNodeList = limitsNode.SelectNodes("*");
                                        if (membershipLimitNodeList == null)
                                        {
                                            throw new Exception("error in membership limits section");
                                        }
                                        foreach (XmlNode servicesLimitNode in membershipLimitNodeList)
                                        {
                                            switch (servicesLimitNode.Name.ToLowerInvariant())
                                            {
                                                case ConfigurationKeys.SWEEP:
                                                    if (
                                                        !int.TryParse(servicesLimitNode.InnerText,
                                                            out MEMBERSHIP_SWEEP_INTERVAL))
                                                    {
                                                        throw new Exception("error in membership limits section");
                                                    }
                                                    break;
                                            }
                                        }
                                        break;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.INVALID_CONFIGURATION_FILE), e.Message);
                        }
                    }

                    // Process masters.
                    nodeList = root.SelectNodes("/config/masters/*");
                    if (nodeList != null)
                    {
                        try
                        {
                            foreach (XmlNode mastersNode in nodeList)
                            {
                                Master configMaster = new Master();
                                foreach (XmlNode masterNode in mastersNode.ChildNodes)
                                {
                                    switch (masterNode.Name.ToLowerInvariant())
                                    {
                                        case ConfigurationKeys.FIRST_NAME:
                                            if (string.IsNullOrEmpty(masterNode.InnerText))
                                            {
                                                throw new Exception("error in masters section");
                                            }
                                            configMaster.FirstName = masterNode.InnerText;
                                            break;
                                        case ConfigurationKeys.LAST_NAME:
                                            if (string.IsNullOrEmpty(masterNode.InnerText))
                                            {
                                                throw new Exception("error in masters section");
                                            }
                                            configMaster.LastName = masterNode.InnerText;
                                            break;
                                    }
                                }
                                MASTERS.Add(configMaster);
                            }
                        }
                        catch (Exception e)
                        {
                            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.INVALID_CONFIGURATION_FILE), e.Message);
                        }
                    }

                    // Process groups.
                    nodeList = root.SelectNodes("/config/groups/*");
                    if (nodeList != null)
                    {
                        try
                        {
                            foreach (XmlNode groupsNode in nodeList)
                            {
                                Group configGroup = new Group();
                                foreach (XmlNode groupNode in groupsNode.ChildNodes)
                                {
                                    switch (groupNode.Name.ToLowerInvariant())
                                    {
                                        case ConfigurationKeys.NAME:
                                            if (string.IsNullOrEmpty(groupNode.InnerText))
                                            {
                                                throw new Exception("error in group section");
                                            }
                                            configGroup.Name = groupNode.InnerText;
                                            break;
                                        case ConfigurationKeys.UUID:
                                            if (!UUID.TryParse(groupNode.InnerText, out configGroup.UUID))
                                            {
                                                throw new Exception("error in group section");
                                            }
                                            break;
                                        case ConfigurationKeys.PASSWORD:
                                            if (string.IsNullOrEmpty(groupNode.InnerText))
                                            {
                                                throw new Exception("error in group section");
                                            }
                                            configGroup.Password = groupNode.InnerText;
                                            break;
                                        case ConfigurationKeys.CHATLOG:
                                            if (string.IsNullOrEmpty(groupNode.InnerText))
                                            {
                                                throw new Exception("error in group section");
                                            }
                                            configGroup.ChatLog = groupNode.InnerText;
                                            break;
                                        case ConfigurationKeys.DATABASE:
                                            if (string.IsNullOrEmpty(groupNode.InnerText))
                                            {
                                                throw new Exception("error in group section");
                                            }
                                            configGroup.DatabaseFile = groupNode.InnerText;
                                            break;
                                        case ConfigurationKeys.PERMISSIONS:
                                            XmlNodeList permissionNodeList = groupNode.SelectNodes("*");
                                            if (permissionNodeList == null)
                                            {
                                                throw new Exception("error in group permission section");
                                            }
                                            uint permissionMask = 0;
                                            foreach (XmlNode permissioNode in permissionNodeList)
                                            {
                                                XmlNode node = permissioNode;
                                                Parallel.ForEach(
                                                    wasGetEnumDescriptions<Permissions>()
                                                        .Where(name => name.Equals(node.Name,
                                                            StringComparison.Ordinal)), name =>
                                                            {
                                                                bool granted;
                                                                if (!bool.TryParse(node.InnerText, out granted))
                                                                {
                                                                    throw new Exception(
                                                                        "error in group permission section");
                                                                }
                                                                if (granted)
                                                                {
                                                                    permissionMask = permissionMask |
                                                                                     (uint)
                                                                                         wasGetEnumValueFromDescription
                                                                                             <Permissions>(name);
                                                                }
                                                            });
                                            }
                                            configGroup.PermissionMask = permissionMask;
                                            break;
                                        case ConfigurationKeys.NOTIFICATIONS:
                                            XmlNodeList notificationNodeList = groupNode.SelectNodes("*");
                                            if (notificationNodeList == null)
                                            {
                                                throw new Exception("error in group notification section");
                                            }
                                            uint notificationMask = 0;
                                            foreach (XmlNode notificationNode in notificationNodeList)
                                            {
                                                XmlNode node = notificationNode;
                                                Parallel.ForEach(
                                                    wasGetEnumDescriptions<Notifications>()
                                                        .Where(name => name.Equals(node.Name,
                                                            StringComparison.Ordinal)), name =>
                                                            {
                                                                bool granted;
                                                                if (!bool.TryParse(node.InnerText, out granted))
                                                                {
                                                                    throw new Exception(
                                                                        "error in group notification section");
                                                                }
                                                                if (granted)
                                                                {
                                                                    notificationMask = notificationMask |
                                                                                       (uint)
                                                                                           wasGetEnumValueFromDescription
                                                                                               <Notifications>(name);
                                                                }
                                                            });
                                            }
                                            configGroup.NotificationMask = notificationMask;
                                            break;
                                    }
                                }
                                GROUPS.Add(configGroup);
                            }
                        }
                        catch (Exception e)
                        {
                            Feedback(wasGetDescriptionFromEnumValue(ConsoleError.INVALID_CONFIGURATION_FILE), e.Message);
                        }
                    }
                }
                Feedback(wasGetDescriptionFromEnumValue(ConsoleError.READ_CONFIGURATION_FILE));
            }
        }

        /// <summary>
        ///     Configuration keys.
        /// </summary>
        private struct ConfigurationKeys
        {
            public const string FIRST_NAME = @"firstname";
            public const string LAST_NAME = @"lastname";
            public const string LOGIN_URL = @"loginurl";
            public const string HTTP = @"http";
            public const string PREFIX = @"prefix";
            public const string TIMEOUT = @"timeout";
            public const string THROTTLE = @"throttle";
            public const string SERVICES = @"services";
            public const string TOS_ACCEPTED = @"tosaccepted";
            public const string AUTO_ACTIVATE_GROUP = @"autoactivategroup";
            public const string GROUP_CREATE_FEE = @"groupcreatefee";
            public const string START_LOCATION = @"startlocation";
            public const string LOG = @"log";
            public const string NAME = @"name";
            public const string UUID = @"uuid";
            public const string PASSWORD = @"password";
            public const string CHATLOG = @"chatlog";
            public const string DATABASE = @"database";
            public const string PERMISSIONS = @"permissions";
            public const string NOTIFICATIONS = @"notifications";
            public const string CALLBACKS = @"callbacks";
            public const string QUEUE_LENGTH = @"queuelength";
            public const string CLIENT = @"client";
            public const string NAGGLE = @"naggle";
            public const string CONNECTIONS = @"connections";
            public const string EXPECT100CONTINUE = @"expect100continue";
            public const string MAC = @"MAC";
            public const string SERVER = @"server";
            public const string MEMBERSHIP = @"membership";
            public const string SWEEP = @"sweep";
            public const string ENABLE = @"enable";
            public const string REBAKE = @"rebake";
            public const string ACTIVATE = @"activate";
        }

        /// <summary>
        ///     Structure containing error messages printed on console for the owner.
        /// </summary>
        private enum ConsoleError
        {
            [Description("none")] NONE = 0,
            [Description("access denied")] ACCESS_DENIED = 1,
            [Description("invalid configuration file")] INVALID_CONFIGURATION_FILE,

            [Description(
                "the Terms of Service (TOS) for the grid you are connecting to have not been accepted, please check your configuration file"
                )] TOS_NOT_ACCEPTED,
            [Description("teleport failed")] TELEPORT_FAILED,
            [Description("teleport succeeded")] TELEPORT_SUCCEEDED,
            [Description("accepted friendship")] ACCEPTED_FRIENDSHIP,
            [Description("login failed")] LOGIN_FAILED,
            [Description("login succeeded")] LOGIN_SUCCEEDED,
            [Description("failed to set appearance")] APPEARANCE_SET_FAILED,
            [Description("appearance set")] APPEARANCE_SET_SUCCEEDED,
            [Description("all simulators disconnected")] ALL_SIMULATORS_DISCONNECTED,
            [Description("simulator connected")] SIMULATOR_CONNECTED,
            [Description("event queue started")] EVENT_QUEUE_STARTED,
            [Description("disconnected")] DISCONNECTED,
            [Description("logging out")] LOGGING_OUT,
            [Description("logging in")] LOGGING_IN,
            [Description("could not write to group chat logfile")] COULD_NOT_WRITE_TO_GROUP_CHAT_LOGFILE,
            [Description("agent not found")] AGENT_NOT_FOUND,
            [Description("read configuration file")] READ_CONFIGURATION_FILE,
            [Description("configuration file modified")] CONFIGURATION_FILE_MODIFIED,
            [Description("HTTP server error")] HTTP_SERVER_ERROR,
            [Description("HTTP server not supported")] HTTP_SERVER_NOT_SUPPORTED,
            [Description("starting HTTP server")] STARTING_HTTP_SERVER,
            [Description("stopping HTTP server")] STOPPING_HTTP_SERVER,
            [Description("HTTP server processing aborted")] HTTP_SERVER_PROCESSING_ABORTED,
            [Description("timeout logging out")] TIMEOUT_LOGGING_OUT,
            [Description("callback error")] CALLBACK_ERROR,
            [Description("notification error")] NOTIFICATION_ERROR,
            [Description("inventory cache items loaded")] INVENTORY_CACHE_ITEMS_LOADED,
            [Description("inventory cache items saved")] INVENTORY_CACHE_ITEMS_SAVED,
            [Description("unable to load Corrade cache")] UNABLE_TO_LOAD_CORRADE_CACHE,
            [Description("unable to save Corrade cache")] UNABLE_TO_SAVE_CORRADE_CACHE,
            [Description("failed to manifest RLV behaviour")] FAILED_TO_MANIFEST_RLV_BEHAVIOUR,
            [Description("behaviour not implemented")] BEHAVIOUR_NOT_IMPLEMENTED,
            [Description("failed to activate land group")] FAILED_TO_ACTIVATE_LAND_GROUP
        }

        /// <summary>
        ///     Directions in 3D cartesian.
        /// </summary>
        private enum Direction : uint
        {
            [Description("none")] NONE = 0,
            [Description("back")] BACK,
            [Description("forward")] FORWARD,
            [Description("left")] LEFT,
            [Description("right")] RIGHT,
            [Description("up")] UP,
            [Description("down")] DOWN
        }

        /// <summary>
        ///     Possible entities.
        /// </summary>
        private enum Entity : uint
        {
            [Description("none")] NONE = 0,
            [Description("avatar")] AVATAR,
            [Description("local")] LOCAL,
            [Description("group")] GROUP,
            [Description("estate")] ESTATE,
            [Description("region")] REGION,
            [Description("object")] OBJECT,
            [Description("parcel")] PARCEL
        }

        /// <summary>
        ///     Group structure.
        /// </summary>
        private struct Group
        {
            public string ChatLog;
            public string DatabaseFile;
            public string Name;
            public uint NotificationMask;
            public string Password;
            public uint PermissionMask;
            public UUID UUID;
        }

        /// <summary>
        ///     A structure for group invites.
        /// </summary>
        private struct GroupInvite
        {
            public Agent Agent;
            [Description("fee")] public int Fee;
            [Description("group")] public string Group;
            [Description("session")] public UUID Session;
        }

        /// <summary>
        ///     An event for the group membership notification.
        /// </summary>
        public class GroupMembershipEventArgs : EventArgs
        {
            public Action Action;
            public string AgentName;
            public UUID AgentUUID;
        }

        /// <summary>
        ///     Linden constants.
        /// </summary>
        private struct LINDEN_CONSTANTS
        {
            public struct ALERTS
            {
                public const string NO_ROOM_TO_SIT_HERE = @"No room to sit here, try another spot.";

                public const string UNABLE_TO_SET_HOME =
                    @"You can only set your 'Home Location' on your land or at a mainland Infohub.";

                public const string HOME_SET = @"Home position set.";
            }

            public struct ASSETS
            {
                public struct NOTECARD
                {
                    public const string NEWLINE = "\n";
                }
            }

            public struct AVATARS
            {
                public const int SET_DISPLAY_NAME_SUCCESS = 200;
                public const string LASTNAME_PLACEHOLDER = @"Resident";
            }

            public struct DIRECTORY
            {
                public struct EVENT
                {
                    public const int SEARCH_RESULTS_COUNT = 200;
                }

                public struct GROUP
                {
                    public const int SEARCH_RESULTS_COUNT = 100;
                }

                public struct LAND
                {
                    public const int SEARCH_RESULTS_COUNT = 100;
                }

                public struct PEOPLE
                {
                    public const int SEARCH_RESULTS_COUNT = 100;
                }
            }

            public struct ESTATE
            {
                public const int REGION_RESTART_DELAY = 120;

                public struct MESSAGES
                {
                    public const string REGION_RESTART_MESSAGE = @"restart";
                }
            }

            public struct GRID
            {
                public const string SECOND_LIFE = @"Second Life";
            }

            public struct GROUPS
            {
                public const int MAXIMUM_NUMBER_OF_ROLES = 10;
            }

            public struct LSL
            {
                public const string CSV_DELIMITER = @", ";
                public const float SENSOR_RANGE = 96;
            }
        }

        /// <summary>
        ///     Masters structure.
        /// </summary>
        private struct Master
        {
            public string FirstName;
            public string LastName;
        }

        /// <summary>
        ///     A Corrade notification.
        /// </summary>
        private struct Notification
        {
            public string GROUP;
            public uint NOTIFICATION_MASK;
            public string URL;
        }

        /// <summary>
        ///     An element from the notification queue waiting to be dispatched.
        /// </summary>
        private struct NotificationQueueElement
        {
            public string URL;
            public Dictionary<string, string> message;
        }

        /// <summary>
        ///     Corrade notification types.
        /// </summary>
        [Flags]
        private enum Notifications : uint
        {
            [Description("alert")] NOTIFICATION_ALERT_MESSAGE = 1,
            [Description("region")] NOTIFICATION_REGION_MESSAGE = 2,
            [Description("group")] NOTIFICATION_GROUP_MESSAGE = 4,
            [Description("balance")] NOTIFICATION_BALANCE = 8,
            [Description("message")] NOTIFICATION_INSTANT_MESSAGE = 16,
            [Description("notice")] NOTIFICATION_GROUP_NOTICE = 32,
            [Description("local")] NOTIFICATION_LOCAL_CHAT = 64,
            [Description("dialog")] NOTIFICATION_SCRIPT_DIALOG = 128,
            [Description("friendship")] NOTIFICATION_FRIENDSHIP = 256,
            [Description("inventory")] NOTIFICATION_INVENTORY = 512,
            [Description("permission")] NOTIFICATION_SCRIPT_PERMISSION = 1024,
            [Description("lure")] NOTIFICATION_TELEPORT_LURE = 2048,
            [Description("effect")] NOTIFICATION_VIEWER_EFFECT = 4096,
            [Description("collision")] NOTIFICATION_MEAN_COLLISION = 8192,
            [Description("crossing")] NOTIFICATION_REGION_CROSSED = 16384,
            [Description("terse")] NOTIFICATION_TERSE_UPDATES = 32768,
            [Description("typing")] NOTIFICATION_TYPING = 65536,
            [Description("invite")] NOTIFICATION_GROUP_INVITE = 131072,
            [Description("economy")] NOTIFICATION_ECONOMY = 262144,
            [Description("membership")] NOTIFICATION_GROUP_MEMBERSHIP = 524288
        }

        /// <summary>
        ///     Corrade permissions.
        /// </summary>
        [Flags]
        private enum Permissions : uint
        {
            [Description("movement")] PERMISSION_MOVEMENT = 1,
            [Description("economy")] PERMISSION_ECONOMY = 2,
            [Description("land")] PERMISSION_LAND = 4,
            [Description("grooming")] PERMISSION_GROOMING = 8,
            [Description("inventory")] PERMISSION_INVENTORY = 16,
            [Description("interact")] PERMISSION_INTERACT = 32,
            [Description("mute")] PERMISSION_MUTE = 64,
            [Description("database")] PERMISSION_DATABASE = 128,
            [Description("notifications")] PERMISSION_NOTIFICATIONS = 256,
            [Description("talk")] PERMISSION_TALK = 512,
            [Description("directory")] PERMISSION_DIRECTORY = 1024,
            [Description("system")] PERMISSION_SYSTEM = 2048,
            [Description("friendship")] PERMISSION_FRIENDSHIP = 4096,
            [Description("execute")] PERMISSION_EXECUTE = 8192,
            [Description("group")] PERMISSION_GROUP = 16384
        }

        /// <summary>
        ///     Keys returned by Corrade.
        /// </summary>
        private enum ResultKeys : uint
        {
            [Description("none")] NONE = 0,
            [Description("data")] DATA,
            [Description("success")] SUCCESS,
            [Description("error")] ERROR
        }

        /// <summary>
        ///     A structure for script dialogs.
        /// </summary>
        private struct ScriptDialog
        {
            public Agent Agent;
            [Description("button")] public List<string> Button;
            [Description("channel")] public int Channel;
            [Description("item")] public UUID Item;
            [Description("message")] public string Message;
            [Description("name")] public string Name;
        }

        /// <summary>
        ///     Structure containing errors returned to scripts.
        /// </summary>
        private enum ScriptError
        {
            [Description("none")] NONE = 0,
            [Description("could not join group")] COULD_NOT_JOIN_GROUP,
            [Description("could not leave group")] COULD_NOT_LEAVE_GROUP,
            [Description("agent not found")] AGENT_NOT_FOUND,
            [Description("group not found")] GROUP_NOT_FOUND,
            [Description("already in group")] ALREADY_IN_GROUP,
            [Description("not in group")] NOT_IN_GROUP,
            [Description("role not found")] ROLE_NOT_FOUND,
            [Description("command not found")] COMMAND_NOT_FOUND,
            [Description("could not eject agent")] COULD_NOT_EJECT_AGENT,
            [Description("no group power for command")] NO_GROUP_POWER_FOR_COMMAND,
            [Description("cannot eject owners")] CANNOT_EJECT_OWNERS,
            [Description("inventory item not found")] INVENTORY_ITEM_NOT_FOUND,
            [Description("invalid pay amount")] INVALID_PAY_AMOUNT,
            [Description("insufficient funds")] INSUFFICIENT_FUNDS,
            [Description("invalid pay target")] INVALID_PAY_TARGET,
            [Description("teleport failed")] TELEPORT_FAILED,
            [Description("primitive not found")] PRIMITIVE_NOT_FOUND,
            [Description("could not sit")] COULD_NOT_SIT,
            [Description("no Corrade permissions")] NO_CORRADE_PERMISSIONS,
            [Description("could not create group")] COULD_NOT_CREATE_GROUP,
            [Description("could not create role")] COULD_NOT_CREATE_ROLE,
            [Description("no role name specified")] NO_ROLE_NAME_SPECIFIED,
            [Description("timeout getting group roles members")] TIMEOUT_GETING_GROUP_ROLES_MEMBERS,
            [Description("timeout getting group roles")] TIMEOUT_GETTING_GROUP_ROLES,
            [Description("timeout getting role powers")] TIMEOUT_GETTING_ROLE_POWERS,
            [Description("could not find parcel")] COULD_NOT_FIND_PARCEL,
            [Description("unable to set home")] UNABLE_TO_SET_HOME,
            [Description("unable to go home")] UNABLE_TO_GO_HOME,
            [Description("timeout getting profile")] TIMEOUT_GETTING_PROFILE,
            [Description("texture not found")] TEXTURE_NOT_FOUND,
            [Description("type can only be voice or text")] TYPE_CAN_BE_VOICE_OR_TEXT,
            [Description("agent not in group")] AGENT_NOT_IN_GROUP,
            [Description("empty attachments")] EMPTY_ATTACHMENTS,
            [Description("could not get land users")] COULD_NOT_GET_LAND_USERS,
            [Description("no region specified")] NO_REGION_SPECIFIED,
            [Description("empty pick name")] EMPTY_PICK_NAME,
            [Description("unable to join group chat")] UNABLE_TO_JOIN_GROUP_CHAT,
            [Description("invalid position")] INVALID_POSITION,
            [Description("could not find title")] COULD_NOT_FIND_TITLE,
            [Description("fly action can only be start or stop")] FLY_ACTION_START_OR_STOP,
            [Description("invalid proposal text")] INVALID_PROPOSAL_TEXT,
            [Description("invalid proposal quorum")] INVALID_PROPOSAL_QUORUM,
            [Description("invalid proposal majority")] INVALID_PROPOSAL_MAJORITY,
            [Description("invalid proposal duration")] INVALID_PROPOSAL_DURATION,
            [Description("invalid mute target")] INVALID_MUTE_TARGET,
            [Description("unknown action")] UNKNOWN_ACTION,
            [Description("no database file configured")] NO_DATABASE_FILE_CONFIGURED,
            [Description("no database key specified")] NO_DATABASE_KEY_SPECIFIED,
            [Description("no database value specified")] NO_DATABASE_VALUE_SPECIFIED,
            [Description("unknown database action")] UNKNOWN_DATABASE_ACTION,
            [Description("cannot remove owner role")] CANNOT_REMOVE_OWNER_ROLE,
            [Description("cannot remove user from owner role")] CANNOT_REMOVE_USER_FROM_OWNER_ROLE,
            [Description("timeout getting picks")] TIMEOUT_GETTING_PICKS,
            [Description("maximum number of roles exceeded")] MAXIMUM_NUMBER_OF_ROLES_EXCEEDED,
            [Description("cannot delete a group member from the everyone role")] CANNOT_DELETE_A_GROUP_MEMBER_FROM_THE_EVERYONE_ROLE,
            [Description("group members are by default in the everyone role")] GROUP_MEMBERS_ARE_BY_DEFAULT_IN_THE_EVERYONE_ROLE,
            [Description("cannot delete the everyone role")] CANNOT_DELETE_THE_EVERYONE_ROLE,
            [Description("invalid url provided")] INVALID_URL_PROVIDED,
            [Description("invalid notification types")] INVALID_NOTIFICATION_TYPES,
            [Description("unknown notifications action")] UNKNOWN_NOTIFICATIONS_ACTION,
            [Description("notification not allowed")] NOTIFICATION_NOT_ALLOWED,
            [Description("no range provided")] NO_RANGE_PROVIDED,
            [Description("unknwon directory search type")] UNKNOWN_DIRECTORY_SEARCH_TYPE,
            [Description("no search text provided")] NO_SEARCH_TEXT_PROVIDED,
            [Description("unknwon restart action")] UNKNOWN_RESTART_ACTION,
            [Description("unknown move action")] UNKNOWN_MOVE_ACTION,
            [Description("timeout getting top scripts")] TIMEOUT_GETTING_TOP_SCRIPTS,
            [Description("timeout waiting for estate list")] TIMEOUT_WAITING_FOR_ESTATE_LIST,
            [Description("unknwon top type")] UNKNOWN_TOP_TYPE,
            [Description("unknown estate list action")] UNKNOWN_ESTATE_LIST_ACTION,
            [Description("unknown estate list")] UNKNOWN_ESTATE_LIST,
            [Description("no item specified")] NO_ITEM_SPECIFIED,
            [Description("unknown animation action")] UNKNOWN_ANIMATION_ACTION,
            [Description("no channel specified")] NO_CHANNEL_SPECIFIED,
            [Description("no button index specified")] NO_BUTTON_INDEX_SPECIFIED,
            [Description("no button specified")] NO_BUTTON_SPECIFIED,
            [Description("no land rights")] NO_LAND_RIGHTS,
            [Description("unknown entity")] UNKNOWN_ENTITY,
            [Description("invalid rotation")] INVALID_ROTATION,
            [Description("could not set script state")] COULD_NOT_SET_SCRIPT_STATE,
            [Description("item is not a script")] ITEM_IS_NOT_A_SCRIPT,
            [Description("avatar not in range")] AVATAR_NOT_IN_RANGE,
            [Description("failed to get display name")] FAILED_TO_GET_DISPLAY_NAME,
            [Description("no name provided")] NO_NAME_PROVIDED,
            [Description("could not set display name")] COULD_NOT_SET_DISPLAY_NAME,
            [Description("timeout joining group")] TIMEOUT_JOINING_GROUP,
            [Description("timeout creating group")] TIMEOUT_CREATING_GROUP,
            [Description("timeout ejecting agent")] TIMEOUT_EJECTING_AGENT,
            [Description("timeout getting group role members")] TIMEOUT_GETTING_GROUP_ROLE_MEMBERS,
            [Description("timeout leaving group")] TIMEOUT_LEAVING_GROUP,
            [Description("timeout joining group chat")] TIMEOUT_JOINING_GROUP_CHAT,
            [Description("timeout during teleport")] TIMEOUT_DURING_TELEPORT,
            [Description("timeout requesting sit")] TIMEOUT_REQUESTING_SIT,
            [Description("timeout getting land users")] TIMEOUT_GETTING_LAND_USERS,
            [Description("timeout getting script state")] TIMEOUT_GETTING_SCRIPT_STATE,
            [Description("timeout updating mute list")] TIMEOUT_UPDATING_MUTE_LIST,
            [Description("timeout getting parcels")] TIMEOUT_GETTING_PARCELS,
            [Description("empty classified name")] EMPTY_CLASSIFIED_NAME,
            [Description("invalid price")] INVALID_PRICE,
            [Description("timeout getting classifieds")] TIMEOUT_GETTING_CLASSIFIEDS,
            [Description("could not find classified")] COULD_NOT_FIND_CLASSIFIED,
            [Description("invalid days")] INVALID_DAYS,
            [Description("invalid interval")] INVALID_INTERVAL,
            [Description("timeout getting group account summary")] TIMEOUT_GETTING_GROUP_ACCOUNT_SUMMARY,
            [Description("friend not found")] FRIEND_NOT_FOUND,
            [Description("the agent already is a friend")] AGENT_ALREADY_FRIEND,
            [Description("no friendship offer found")] NO_FRIENDSHIP_OFFER_FOUND,
            [Description("friend does not allow mapping")] FRIEND_DOES_NOT_ALLOW_MAPPING,
            [Description("timeout mapping friend")] TIMEOUT_MAPPING_FRIEND,
            [Description("friend offline")] FRIEND_OFFLINE,
            [Description("timeout getting region")] TIMEOUT_GETTING_REGION,
            [Description("region not found")] REGION_NOT_FOUND,
            [Description("no map items found")] NO_MAP_ITEMS_FOUND,
            [Description("no description provided")] NO_DESCRIPTION_PROVIDED,
            [Description("no folder specified")] NO_FOLDER_SPECIFIED,
            [Description("empty wearables")] EMPTY_WEARABLES,
            [Description("parcel not for sale")] PARCEL_NOT_FOR_SALE,
            [Description("unknown access list type")] UNKNOWN_ACCESS_LIST_TYPE,
            [Description("no task specified")] NO_TASK_SPECIFIED,
            [Description("timeout getting group members")] TIMEOUT_GETTING_GROUP_MEMBERS,
            [Description("group not open")] GROUP_NOT_OPEN,
            [Description("timeout downloading terrain")] TIMEOUT_DOWNLOADING_ASSET,
            [Description("timeout uploading terrain")] TIMEOUT_UPLOADING_ASSET,
            [Description("empty terrain data")] EMPTY_ASSET_DATA,
            [Description("the specified folder contains no equipable items")] NO_EQUIPABLE_ITEMS,
            [Description("inventory offer not found")] INVENTORY_OFFER_NOT_FOUND,
            [Description("no session specified")] NO_SESSION_SPECIFIED,
            [Description("folder not found")] FOLDER_NOT_FOUND,
            [Description("timeout creating item")] TIMEOUT_CREATING_ITEM,
            [Description("timeout uploading item")] TIMEOUT_UPLOADING_ITEM,
            [Description("unable to upload item")] UNABLE_TO_UPLOAD_ITEM,
            [Description("unable to create item")] UNABLE_TO_CREATE_ITEM,
            [Description("timeout uploading item data")] TIMEOUT_UPLOADING_ITEM_DATA,
            [Description("unable to upload item data")] UNABLE_TO_UPLOAD_ITEM_DATA,
            [Description("unknown direction")] UNKNOWN_DIRECTION,
            [Description("timeout requesting to set home")] TIMEOUT_REQUESTING_TO_SET_HOME,
            [Description("timeout traferring asset")] TIMEOUT_TRANSFERRING_ASSET,
            [Description("asset upload failed")] ASSET_UPLOAD_FAILED,
            [Description("failed to download asset")] FAILED_TO_DOWNLOAD_ASSET,
            [Description("unknown asset type")] UNKNOWN_ASSET_TYPE,
            [Description("invalid asset data")] INVALID_ASSET_DATA,
            [Description("unknown wearable type")] UNKNOWN_WEARABLE_TYPE,
            [Description("unknown inventory type")] UNKNOWN_INVENTORY_TYPE,
            [Description("could not compile regular expression")] COULD_NOT_COMPILE_REGULAR_EXPRESSION,
            [Description("no pattern provided")] NO_PATTERN_PROVIDED,
            [Description("no executable file provided")] NO_EXECUTABLE_FILE_PROVIDED,
            [Description("timeout waiting for execution")] TIMEOUT_WAITING_FOR_EXECUTION,
            [Description("unknown group invite session")] UNKNOWN_GROUP_INVITE_SESSION,
            [Description("unable to obtain money balance")] UNABLE_TO_OBTAIN_MONEY_BALANCE,
            [Description("rebake failed")] REBAKE_FAILED
        }

        /// <summary>
        ///     Keys reconigzed by Corrade.
        /// </summary>
        private enum ScriptKeys : uint
        {
            [Description("none")] NONE = 0,
            [Description("rlv")] RLV,
            [Description("getinventorypath")] GETINVENTORYPATH,
            [Description("committed")] COMMITTED,
            [Description("credit")] CREDIT,
            [Description("success")] SUCCESS,
            [Description("transaction")] TRANSACTION,
            [Description("getscriptdialogs")] GETSCRIPTDIALOGS,
            [Description("getscriptpermissionrequests")] GETSCRIPTPERMISSIONREQUESTS,
            [Description("getteleportlures")] GETTELEPORTLURES,
            [Description("replytogroupinvite")] REPLYTOGROUPINVITE,
            [Description("getgroupinvites")] GETGROUPINVITES,
            [Description("getmemberroles")] GETMEMBERROLES,
            [Description("execute")] EXECUTE,
            [Description("parameter")] PARAMETER,
            [Description("file")] FILE,
            [Description("cache")] CACHE,
            [Description("getgridregiondata")] GETGRIDREGIONDATA,
            [Description("getregionparcelsboundingbox")] GETREGIONPARCELSBOUNDINGBOX,
            [Description("pattern")] PATTERN,
            [Description("searchinventory")] SEARCHINVENTORY,
            [Description("getterrainheight")] GETTERRAINHEIGHT,
            [Description("northeast")] NORTHEAST,
            [Description("southwest")] SOUTHWEST,
            [Description("configuration")] CONFIGURATION,
            [Description("upload")] UPLOAD,
            [Description("download")] DOWNLOAD,
            [Description("setparceldata")] SETPARCELDATA,
            [Description("new")] NEW,
            [Description("old")] OLD,
            [Description("aggressor")] AGGRESSOR,
            [Description("magnitude")] MAGNITUDE,
            [Description("time")] TIME,
            [Description("victim")] VICTIM,
            [Description("playgesture")] PLAYGESTURE,
            [Description("jump")] JUMP,
            [Description("crouch")] CROUCH,
            [Description("turnto")] TURNTO,
            [Description("nudge")] NUDGE,
            [Description("createnotecard")] CREATENOTECARD,
            [Description("direction")] DIRECTION,
            [Description("agent")] AGENT,
            [Description("replytoinventoryoffer")] REPLYTOINVENTORYOFFER,
            [Description("getinventoryoffers")] GETINVENTORYOFFERS,
            [Description("updateprimitiveinventory")] UPDATEPRIMITIVEINVENTORY,
            [Description("version")] VERSION,
            [Description("playsound")] PLAYSOUND,
            [Description("gain")] GAIN,
            [Description("getrolemembers")] GETROLEMEMBERS,
            [Description("status")] STATUS,
            [Description("getmembers")] GETMEMBERS,
            [Description("replytoteleportlure")] REPLYTOTELEPORTLURE,
            [Description("session")] SESSION,
            [Description("replytoscriptpermissionrequest")] REPLYTOSCRIPTPERMISSIONREQUEST,
            [Description("task")] TASK,
            [Description("getparcellist")] GETPARCELLIST,
            [Description("parcelrelease")] PARCELRELEASE,
            [Description("parcelbuy")] PARCELBUY,
            [Description("removecontribution")] REMOVECONTRIBUTION,
            [Description("forgroup")] FORGROUP,
            [Description("parceldeed")] PARCELDEED,
            [Description("parcelreclaim")] PARCELRECLAIM,
            [Description("unwear")] UNWEAR,
            [Description("wear")] WEAR,
            [Description("wearables")] WEARABLES,
            [Description("getwearables")] GETWEARABLES,
            [Description("changeappearance")] CHANGEAPPEARANCE,
            [Description("folder")] FOLDER,
            [Description("replace")] REPLACE,
            [Description("setobjectrotation")] SETOBJECTROTATION,
            [Description("setobjectdescription")] SETOBJECTDESCRIPTION,
            [Description("setobjectname")] SETOBJECTNAME,
            [Description("setobjectposition")] SETOBJECTPOSITION,
            [Description("setobjectsaleinfo")] SETOBJECTSALEINFO,
            [Description("setobjectgroup")] SETOBJECTGROUP,
            [Description("objectdeed")] OBJECTDEED,
            [Description("setobjectpermissions")] SETOBJECTPERMISSIONS,
            [Description("who")] WHO,
            [Description("permissions")] PERMISSIONS,
            [Description("getavatarpositions")] GETAVATARPOSITIONS,
            [Description("getprimitives")] GETPRIMITIVES,
            [Description("delay")] DELAY,
            [Description("asset")] ASSET,
            [Description("setregiondebug")] SETREGIONDEBUG,
            [Description("scripts")] SCRIPTS,
            [Description("collisions")] COLLISIONS,
            [Description("physics")] PHYSICS,
            [Description("getmapavatarpositions")] GETMAPAVATARPOSITIONS,
            [Description("mapfriend")] MAPFRIEND,
            [Description("replytofriendshiprequest")] REPLYTOFRIENDSHIPREQUEST,
            [Description("getfriendshiprequests")] GETFRIENDSHIPREQUESTS,
            [Description("grantfriendrights")] GRANTFRIENDRIGHTS,
            [Description("rights")] RIGHTS,
            [Description("getfriendslist")] GETFRIENDSLIST,
            [Description("terminatefriendship")] TERMINATEFRIENDSHIP,
            [Description("offerfriendship")] OFFERFRIENDSHIP,
            [Description("getfrienddata")] GETFRIENDDATA,
            [Description("days")] DAYS,
            [Description("interval")] INTERVAL,
            [Description("getgroupaccountsummarydata")] GETGROUPACCOUNTSUMMARYDATA,
            [Description("getselfdata")] GETSELFDATA,
            [Description("deleteclassified")] DELETECLASSIFIED,
            [Description("addclassified")] ADDCLASSIFIED,
            [Description("price")] PRICE,
            [Description("renew")] RENEW,
            [Description("logout")] LOGOUT,
            [Description("displayname")] DISPLAYNAME,
            [Description("returnprimitives")] RETURNPRIMITIVES,
            [Description("getgroupdata")] GETGROUPDATA,
            [Description("getavatardata")] GETAVATARDATA,
            [Description("getprimitiveinventory")] GETPRIMITIVEINVENTORY,
            [Description("getinventorydata")] GETINVENTORYDATA,
            [Description("getprimitiveinventorydata")] GETPRIMITIVEINVENTORYDATA,
            [Description("getscriptrunning")] GETSCRIPTRUNNING,
            [Description("setscriptrunning")] SETSCRIPTRUNNING,
            [Description("derez")] DEREZ,
            [Description("getparceldata")] GETPARCELDATA,
            [Description("rez")] REZ,
            [Description("rotation")] ROTATION,
            [Description("index")] INDEX,
            [Description("replytoscriptdialog")] REPLYTOSCRIPTDIALOG,
            [Description("owner")] OWNER,
            [Description("button")] BUTTON,
            [Description("getanimations")] GETANIMATIONS,
            [Description("animation")] ANIMATION,
            [Description("setestatelist")] SETESTATELIST,
            [Description("getestatelist")] GETESTATELIST,
            [Description("all")] ALL,
            [Description("getregiontop")] GETREGIONTOP,
            [Description("restartregion")] RESTARTREGION,
            [Description("timeout")] TIMEOUT,
            [Description("directorysearch")] DIRECTORYSEARCH,
            [Description("getprofiledata")] GETPROFILEDATA,
            [Description("getparticlesystem")] GETPARTICLESYSTEM,
            [Description("data")] DATA,
            [Description("range")] RANGE,
            [Description("balance")] BALANCE,
            [Description("key")] KEY,
            [Description("value")] VALUE,
            [Description("database")] DATABASE,
            [Description("text")] TEXT,
            [Description("quorum")] QUORUM,
            [Description("majority")] MAJORITY,
            [Description("startproposal")] STARTPROPOSAL,
            [Description("duration")] DURATION,
            [Description("action")] ACTION,
            [Description("deletefromrole")] DELETEFROMROLE,
            [Description("addtorole")] ADDTOROLE,
            [Description("leave")] LEAVE,
            [Description("updategroupdata")] UPDATEGROUPDATA,
            [Description("eject")] EJECT,
            [Description("invite")] INVITE,
            [Description("join")] JOIN,
            [Description("callback")] CALLBACK,
            [Description("group")] GROUP,
            [Description("password")] PASSWORD,
            [Description("firstname")] FIRSTNAME,
            [Description("lastname")] LASTNAME,
            [Description("command")] COMMAND,
            [Description("role")] ROLE,
            [Description("title")] TITLE,
            [Description("tell")] TELL,
            [Description("notice")] NOTICE,
            [Description("message")] MESSAGE,
            [Description("subject")] SUBJECT,
            [Description("item")] ITEM,
            [Description("pay")] PAY,
            [Description("amount")] AMOUNT,
            [Description("target")] TARGET,
            [Description("reason")] REASON,
            [Description("getbalance")] GETBALANCE,
            [Description("teleport")] TELEPORT,
            [Description("region")] REGION,
            [Description("position")] POSITION,
            [Description("getregiondata")] GETREGIONDATA,
            [Description("sit")] SIT,
            [Description("stand")] STAND,
            [Description("ban")] BAN,
            [Description("parceleject")] PARCELEJECT,
            [Description("creategroup")] CREATEGROUP,
            [Description("parcelfreeze")] PARCELFREEZE,
            [Description("createrole")] CREATEROLE,
            [Description("deleterole")] DELETEROLE,
            [Description("getrolesmembers")] GETROLESMEMBERS,
            [Description("getroles")] GETROLES,
            [Description("getrolepowers")] GETROLEPOWERS,
            [Description("powers")] POWERS,
            [Description("lure")] LURE,
            [Description("URL")] URL,
            [Description("sethome")] SETHOME,
            [Description("gohome")] GOHOME,
            [Description("setprofiledata")] SETPROFILEDATA,
            [Description("give")] GIVE,
            [Description("deleteitem")] DELETEITEM,
            [Description("emptytrash")] EMPTYTRASH,
            [Description("fly")] FLY,
            [Description("addpick")] ADDPICK,
            [Description("deltepick")] DELETEPICK,
            [Description("touch")] TOUCH,
            [Description("moderate")] MODERATE,
            [Description("type")] TYPE,
            [Description("silence")] SILENCE,
            [Description("freeze")] FREEZE,
            [Description("rebake")] REBAKE,
            [Description("getattachments")] GETATTACHMENTS,
            [Description("attach")] ATTACH,
            [Description("attachments")] ATTACHMENTS,
            [Description("detach")] DETACH,
            [Description("getprimitiveowners")] GETPRIMITIVEOWNERS,
            [Description("entity")] ENTITY,
            [Description("channel")] CHANNEL,
            [Description("name")] NAME,
            [Description("description")] DESCRIPTION,
            [Description("getprimitivedata")] GETPRIMITIVEDATA,
            [Description("activate")] ACTIVATE,
            [Description("move")] MOVE,
            [Description("settitle")] SETTITLE,
            [Description("mute")] MUTE,
            [Description("getmutes")] GETMUTES,
            [Description("notify")] NOTIFY,
            [Description("source")] SOURCE,
            [Description("effect")] EFFECT,
            [Description("id")] ID,
            [Description("terrain")] TERRAIN,
        }

        /// <summary>
        ///     A structure for script permission requests.
        /// </summary>
        private struct ScriptPermissionRequest
        {
            public Agent Agent;
            [Description("item")] public UUID Item;
            [Description("name")] public string Name;
            [Description("permission")] public ScriptPermission Permission;
            [Description("task")] public UUID Task;
        }

        /// <summary>
        ///     A structure for teleport lures.
        /// </summary>
        private struct TeleportLure
        {
            public Agent Agent;
            public UUID Session;
        }

        /// <summary>
        ///     Various types.
        /// </summary>
        private enum Type : uint
        {
            [Description("none")] NONE = 0,
            [Description("text")] TEXT,
            [Description("voice")] VOICE,
            [Description("scripts")] SCRIPTS,
            [Description("colliders")] COLLIDERS,
            [Description("ban")] BAN,
            [Description("group")] GROUP,
            [Description("user")] USER,
            [Description("manager")] MANAGER,
            [Description("classified")] CLASSIFIED,
            [Description("event")] EVENT,
            [Description("land")] LAND,
            [Description("people")] PEOPLE,
            [Description("place")] PLACE
        }

        #region RLV STRUCTURES

        /// <summary>
        ///     Regex used to match RLV commands.
        /// </summary>
        private static readonly Regex RLVRegex = new Regex(@"(?<behaviour>[^:=]+)(:(?<option>[^=]*))?=(?<param>\w+)",
            RegexOptions.Compiled);

        /// <summary>
        ///     Holds all the active RLV rules.
        /// </summary>
        private static readonly HashSet<RLVRule> RLVRules = new HashSet<RLVRule>();

        /// <summary>
        ///     Locks down RLV for linear concurrent access.
        /// </summary>
        private static readonly object RLVRuleLock = new object();

        /// <summary>
        ///     RLV Wearables.
        /// </summary>
        private static readonly List<RLVWearable> RLVWearables = new List<RLVWearable>
        {
            new RLVWearable {Name = @"gloves", WearableType = WearableType.Gloves},
            new RLVWearable {Name = @"jacket", WearableType = WearableType.Jacket},
            new RLVWearable {Name = @"pants", WearableType = WearableType.Pants},
            new RLVWearable {Name = @"shirt", WearableType = WearableType.Shirt},
            new RLVWearable {Name = @"shoes", WearableType = WearableType.Shoes},
            new RLVWearable {Name = @"skirt", WearableType = WearableType.Skirt},
            new RLVWearable {Name = @"socks", WearableType = WearableType.Socks},
            new RLVWearable {Name = @"underpants", WearableType = WearableType.Underpants},
            new RLVWearable {Name = @"undershirt", WearableType = WearableType.Undershirt},
            new RLVWearable {Name = @"skin", WearableType = WearableType.Skin},
            new RLVWearable {Name = @"eyes", WearableType = WearableType.Eyes},
            new RLVWearable {Name = @"hair", WearableType = WearableType.Hair},
            new RLVWearable {Name = @"shape", WearableType = WearableType.Shape},
            new RLVWearable {Name = @"alpha", WearableType = WearableType.Alpha},
            new RLVWearable {Name = @"tattoo", WearableType = WearableType.Tattoo},
            new RLVWearable {Name = @"physics", WearableType = WearableType.Physics}
        };

        /// <summary>
        ///     RLV Attachments.
        /// </summary>
        private static readonly List<RLVAttachment> RLVAttachments = new List<RLVAttachment>
        {
            new RLVAttachment {Name = @"none", AttachmentPoint = AttachmentPoint.Default},
            new RLVAttachment {Name = @"chest", AttachmentPoint = AttachmentPoint.Chest},
            new RLVAttachment {Name = @"skull", AttachmentPoint = AttachmentPoint.Skull},
            new RLVAttachment {Name = @"left shoulder", AttachmentPoint = AttachmentPoint.LeftShoulder},
            new RLVAttachment {Name = @"right shoulder", AttachmentPoint = AttachmentPoint.RightShoulder},
            new RLVAttachment {Name = @"left hand", AttachmentPoint = AttachmentPoint.LeftHand},
            new RLVAttachment {Name = @"right hand", AttachmentPoint = AttachmentPoint.RightHand},
            new RLVAttachment {Name = @"left foot", AttachmentPoint = AttachmentPoint.LeftFoot},
            new RLVAttachment {Name = @"right foot", AttachmentPoint = AttachmentPoint.RightFoot},
            new RLVAttachment {Name = @"spine", AttachmentPoint = AttachmentPoint.Spine},
            new RLVAttachment {Name = @"pelvis", AttachmentPoint = AttachmentPoint.Pelvis},
            new RLVAttachment {Name = @"mouth", AttachmentPoint = AttachmentPoint.Mouth},
            new RLVAttachment {Name = @"chin", AttachmentPoint = AttachmentPoint.Chin},
            new RLVAttachment {Name = @"left ear", AttachmentPoint = AttachmentPoint.LeftEar},
            new RLVAttachment {Name = @"right ear", AttachmentPoint = AttachmentPoint.RightEar},
            new RLVAttachment {Name = @"left eyeball", AttachmentPoint = AttachmentPoint.LeftEyeball},
            new RLVAttachment {Name = @"right eyeball", AttachmentPoint = AttachmentPoint.RightEyeball},
            new RLVAttachment {Name = @"nose", AttachmentPoint = AttachmentPoint.Nose},
            new RLVAttachment {Name = @"r upper arm", AttachmentPoint = AttachmentPoint.RightUpperArm},
            new RLVAttachment {Name = @"r forearm", AttachmentPoint = AttachmentPoint.RightForearm},
            new RLVAttachment {Name = @"l upper arm", AttachmentPoint = AttachmentPoint.LeftUpperArm},
            new RLVAttachment {Name = @"l forearm", AttachmentPoint = AttachmentPoint.LeftForearm},
            new RLVAttachment {Name = @"right hip", AttachmentPoint = AttachmentPoint.RightHip},
            new RLVAttachment {Name = @"r upper leg", AttachmentPoint = AttachmentPoint.RightUpperLeg},
            new RLVAttachment {Name = @"r lower leg", AttachmentPoint = AttachmentPoint.RightLowerLeg},
            new RLVAttachment {Name = @"left hip", AttachmentPoint = AttachmentPoint.LeftHip},
            new RLVAttachment {Name = @"l upper leg", AttachmentPoint = AttachmentPoint.LeftUpperLeg},
            new RLVAttachment {Name = @"l lower leg", AttachmentPoint = AttachmentPoint.LeftLowerLeg},
            new RLVAttachment {Name = @"stomach", AttachmentPoint = AttachmentPoint.Stomach},
            new RLVAttachment {Name = @"left pec", AttachmentPoint = AttachmentPoint.LeftPec},
            new RLVAttachment {Name = @"right pec", AttachmentPoint = AttachmentPoint.RightPec},
            new RLVAttachment {Name = @"center 2", AttachmentPoint = AttachmentPoint.HUDCenter2},
            new RLVAttachment {Name = @"top right", AttachmentPoint = AttachmentPoint.HUDTopRight},
            new RLVAttachment {Name = @"top", AttachmentPoint = AttachmentPoint.HUDTop},
            new RLVAttachment {Name = @"top left", AttachmentPoint = AttachmentPoint.HUDTopLeft},
            new RLVAttachment {Name = @"center", AttachmentPoint = AttachmentPoint.HUDCenter},
            new RLVAttachment {Name = @"bottom left", AttachmentPoint = AttachmentPoint.HUDBottomLeft},
            new RLVAttachment {Name = @"bottom", AttachmentPoint = AttachmentPoint.HUDBottom},
            new RLVAttachment {Name = @"bottom right", AttachmentPoint = AttachmentPoint.HUDBottomRight},
            new RLVAttachment {Name = @"neck", AttachmentPoint = AttachmentPoint.Neck},
            new RLVAttachment {Name = @"root", AttachmentPoint = AttachmentPoint.Root}
        };

        /// <summary>
        ///     RLV attachment structure.
        /// </summary>
        private struct RLVAttachment
        {
            public AttachmentPoint AttachmentPoint;
            public string Name;
        }

        /// <summary>
        ///     Enumeration for supported RLV commands.
        /// </summary>
        private enum RLVBehaviour : uint
        {
            [Description("none")] NONE = 0,
            [Description("version")] VERSION,
            [Description("versionnew")] VERSIONNEW,
            [Description("versionnum")] VERSIONNUM,
            [Description("getgroup")] GETGROUP,
            [Description("setgroup")] SETGROUP,
            [Description("getsitid")] GETSITID,
            [Description("getstatusall")] GETSTATUSALL,
            [Description("getstatus")] GETSTATUS,
            [Description("sit")] SIT,
            [Description("unsit")] UNSIT,
            [Description("setrot")] SETROT,
            [Description("tpto")] TPTO,
            [Description("getoutfit")] GETOUTFIT,
            [Description("getattach")] GETATTACH,
            [Description("remattach")] REMATTACH,
            [Description("detach")] DETACH,
            [Description("detachme")] DETACHME,
            [Description("remoutfit")] REMOUTFIT,
            [Description("attach")] ATTACH,
            [Description("attachoverreplace")] ATTACHOVERORREPLACE,
            [Description("attachover")] ATTACHOVER,
            [Description("getinv")] GETINV,
            [Description("getinvworn")] GETINVWORN,
            [Description("getpath")] GETPATH,
            [Description("getpathnew")] GETPATHNEW,
            [Description("findfolder")] FINDFOLDER,
            [Description("clear")] CLEAR,
            [Description("accepttp")] ACCEPTTP,
            [Description("acceptpermission")] ACCEPTPERMISSION
        }

        private struct RLVRule
        {
            public string Behaviour;
            public UUID ObjectUUID;
            public string Option;
            public string Param;
        }

        /// <summary>
        ///     RLV wearable structure.
        /// </summary>
        private struct RLVWearable
        {
            public string Name;
            public WearableType WearableType;
        }

        /// <summary>
        ///     Structure for RLV contants.
        /// </summary>
        private struct RLV_CONSTANTS
        {
            public const string COMMAND_OPERATOR = @"@";
            public const string VIEWER = @"RestrainedLife viewer";
            public const string SHORT_VERSION = @"1.23";
            public const string LONG_VERSION = @"1230100";
            public const string FORCE = @"force";
            public const string FALSE_MARKER = @"0";
            public const string TRUE_MARKER = @"1";
            public const string CSV_DELIMITER = @",";
            public const string DOT_MARKER = @".";
            public const string TILDE_MARKER = @"~";
            public const string PROPORTION_SEPARATOR = @"|";
            public const string SHARED_FOLDER_NAME = @"#RLV";
            public const string AND_OPERATOR = @"&&";
            public const string PATH_SEPARATOR = @"/";
            public const string Y = @"y";
            public const string ADD = @"add";
            public const string N = @"n";
            public const string REM = @"rem";
            public const string STATUS_SEPARATOR = @";";
        }

        #endregion
    }

    public class NativeMethods
    {
        public enum CtrlType
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT,
            CTRL_CLOSE_EVENT,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT
        }

        /// <summary>
        ///     Import console handler for windows.
        /// </summary>
        [DllImport("Kernel32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool SetConsoleCtrlHandler(Corrade.EventHandler handler,
            [MarshalAs(UnmanagedType.U1)] bool add);
    }
}