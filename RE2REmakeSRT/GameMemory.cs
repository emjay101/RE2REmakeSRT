﻿using ProcessMemory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RE2REmakeSRT
{
    public class GameMemory : IDisposable
    {
        // Private Variables
        public ProcessMemory.ProcessMemory memoryAccess;
        private const string IGT_TIMESPAN_STRING_FORMAT = @"hh\:mm\:ss\.fff";

        // Pointers
        public long BaseAddress { get; private set; }
                
        public MultilevelPointer FootSteps { get; private set; }
        public MultilevelPointer EnemyTable { get; private set; }
        
        public MultilevelPointer PointerIGT { get; private set; }
        public MultilevelPointer PointerRank { get; private set; }
        public MultilevelPointer PointerPlayerHP { get; private set; }
        public MultilevelPointer PointerPlayerPoison { get; private set; }

        public MultilevelPointer[] PointerEnemyEntries { get; private set; }
        public MultilevelPointer[] PointerEnemyStatusEntries { get; private set; }
        public MultilevelPointer[] PointerInventoryEntries { get; private set; }

        public int CountEnemyEntries { get; private set; }
        
        // Public Properties
        public int PlayerCurrentHealth { get; private set; }
        public int PlayerMaxHealth { get; private set; }
        public bool PlayerPoisoned { get; private set; }
        public InventoryEntry?[] PlayerInventory { get; private set; }
        public RE2_NPC?[] EnemyHealth { get; private set; }
        public long IGTRunningTimer { get; private set; }
        public long IGTCutsceneTimer { get; private set; }
        public long IGTMenuTimer { get; private set; }
        public long IGTPausedTimer { get; private set; }
        public int Rank { get; private set; }
        public float RankScore { get; private set; }
        public int FootStepsAll { get; private set; }
        public long LEngineDefaultPoolId { get; private set; }
        
        // Public Properties - Calculated
        public long IGTRaw => unchecked(IGTRunningTimer - IGTCutsceneTimer - IGTPausedTimer);
        public long IGTCalculated => unchecked(IGTRaw * 10L);

        const int MAX_ENEMY_COUNT = 32;

        public TimeSpan IGTTimeSpan
        {
            get
            {
                TimeSpan timespanIGT;

                if (IGTCalculated <= TimeSpan.MaxValue.Ticks)
                    timespanIGT = new TimeSpan(IGTCalculated);
                else
                    timespanIGT = new TimeSpan();

                return timespanIGT;
            }
        }
        public string IGTString => IGTTimeSpan.ToString(IGT_TIMESPAN_STRING_FORMAT, CultureInfo.InvariantCulture);


        /// <summary>
        /// 
        /// </summary>
        /// <param name="proc"></param>
        public GameMemory(int pid)
        {
            // Initialize variables to default values.
            PlayerCurrentHealth = 0;
            PlayerMaxHealth = 0;
            PlayerPoisoned = false;
            PlayerInventory = new InventoryEntry?[20];
            EnemyHealth = new RE2_NPC?[MAX_ENEMY_COUNT];
            IGTRunningTimer = 0L;
            IGTCutsceneTimer = 0L;
            IGTMenuTimer = 0L;
            IGTPausedTimer = 0L;
            Rank = 0;
            RankScore = 0f;
            FootStepsAll = 0;

            memoryAccess = new ProcessMemory.ProcessMemory(pid);
            BaseAddress = NativeWrappers.GetProcessBaseAddress(pid, ProcessMemory.PInvoke.ListModules.LIST_MODULES_64BIT).ToInt64(); // Bypass .NET's managed solution for getting this and attempt to get this info ourselves via PInvoke since some users are getting 299 PARTIAL COPY when they seemingly shouldn't. This is built as x64 only and RE2 is x64 only to my knowledge.
            PointerEnemyEntries = new MultilevelPointer[MAX_ENEMY_COUNT];
            PointerEnemyStatusEntries = new MultilevelPointer[MAX_ENEMY_COUNT];            
            //symbol re2.g_LEngineDefaultPoolId is exported and could be resolved using either PE header analysis 
            //or LoadLibrary on the re2 executeable  GetModuleFileNameEx -> LoadLibrary-> GetProcAddress -> UnloadLibrary
            LEngineDefaultPoolId = BaseAddress + 0x700AB38;
            // Setup the pointers.
            PointerIGT = new MultilevelPointer(memoryAccess, LEngineDefaultPoolId + 0xA5DD8, 0x2E0L, 0x218L, 0x610L, 0x710L, 0x60L);
            PointerRank = new MultilevelPointer(memoryAccess, LEngineDefaultPoolId + 0x7FEA0);
            PointerPlayerHP = new MultilevelPointer(memoryAccess, LEngineDefaultPoolId + 0xA5D08, 0x50L, 0x20L);
            PointerPlayerPoison = new MultilevelPointer(memoryAccess, PointerPlayerHP.Address+0xF8L);
            FootSteps = new MultilevelPointer(memoryAccess, LEngineDefaultPoolId + 0x71A48);                 
            EnemyTable = new MultilevelPointer(memoryAccess, LEngineDefaultPoolId + 0x78D78);
            UpdateEnemyTable();

            if (!Program.programSpecialOptions.Flags.HasFlag(ProgramFlags.NoInventory))
            {
                PointerInventoryEntries = new MultilevelPointer[20];
                for (int i = 0; i < PointerInventoryEntries.Length; ++i)
                    PointerInventoryEntries[i] = new MultilevelPointer(memoryAccess, LEngineDefaultPoolId + 0xA5D08L, 0x50L, 0x98L, 0x10L, 0x20L + (i * 0x08), 0x18L);
            }
        }

      //  ] +48] + 88] + 18] + 1a0

        /// <summary>
        /// 
        /// </summary>
        public void UpdatePointers()
        {
            PointerIGT.UpdatePointers();
            PointerPlayerHP.UpdatePointers();
            PointerPlayerPoison.UpdatePointers();
            PointerRank.UpdatePointers();
            FootSteps.UpdatePointers();            
            UpdateEnemyTable();
            UpdateIventory();

        }

        private void UpdateEnemyTable()
        {
            int hp, hpmax;
            bool poisoned;
            EnemyTable?.UpdatePointers();
            CountEnemyEntries = EnemyTable?.DerefInt(0x18) ?? 0;
            if (CountEnemyEntries > 0)
            {
                byte[] npc_list = EnemyTable.DerefByteArray(0x40, CountEnemyEntries * sizeof(long));
                long npc_idx = -1;
                for (int i = 0; i < CountEnemyEntries; ++i)
                {
                    hp = 0;
                    hpmax = 0;
                    poisoned = false;

                    npc_idx = BitConverter.ToInt64(npc_list, i * sizeof(long));
                    if (npc_idx != 0)
                    {
                        if (npc_idx + 0x88L != (PointerEnemyEntries[i]?.Address ?? 0L))
                        {
                            PointerEnemyEntries[i] = new MultilevelPointer(memoryAccess, npc_idx + 0x88L, 0x18L, 0x1A0L);
                            if ((PointerEnemyEntries[i]?.Address ?? 0L) != 0L)
                            {
                                PointerEnemyStatusEntries[i] = new MultilevelPointer(memoryAccess, PointerEnemyEntries[i].Address + 0xF8);
                                hp = PointerEnemyEntries[i]?.DerefInt(0x58) ?? 0;
                                hpmax = PointerEnemyEntries[i]?.DerefInt(0x54) ?? 00;
                                poisoned = (PointerEnemyStatusEntries[i]?.DerefByte(0x258) ?? 0 )== 1;
                            }
                        }
                    }
                    EnemyHealth[i] = new RE2_NPC(hpmax, hp, poisoned);                                    
                }
            }
        }

        /// <summary>
        /// This call refreshes important variables such as IGT, HP and Ammo.
        /// </summary>
        /// <param name="cToken"></param>
        public void RefreshSlim()
        {
            // IGT
            IGTRunningTimer = PointerIGT.DerefLong(0x18);
            IGTCutsceneTimer = PointerIGT.DerefLong(0x20);
            IGTMenuTimer = PointerIGT.DerefLong(0x28);
            IGTPausedTimer = PointerIGT.DerefLong(0x30);                               
        }

        /// <summary>
        /// This call refreshes everything. This should be used less often. Inventory rendering can be more expensive and doesn't change as often.
        /// </summary>
        /// <param name="cToken"></param>
        public void Refresh()
        {
            // Perform slim lookups first.
            RefreshSlim();

            // Other lookups that don't need to update as often.
            // Player HP
            PlayerMaxHealth = PointerPlayerHP.DerefInt(0x54);
            PlayerCurrentHealth = PointerPlayerHP.DerefInt(0x58);
            PlayerPoisoned = PointerPlayerPoison.DerefByte(0x258) == 0x01;
            Rank = PointerRank.DerefInt(0x58);
            RankScore = PointerRank.DerefFloat(0x5C);
            FootStepsAll = FootSteps.DerefInt(0x64);           
            UpdateEnemyTable();
            UpdateIventory();
        }

        private void UpdateIventory()
        {
            // Inventory
            if (!Program.programSpecialOptions.Flags.HasFlag(ProgramFlags.NoInventory))
            {
                for (int i = 0; i < (PointerInventoryEntries?.Length ?? 0); i++)
                {
                    PointerInventoryEntries[i]?.UpdatePointers();
                    long invDataPointer = PointerInventoryEntries[i]?.DerefLong(0x10)??0;
                    if (invDataPointer != 0)
                    {
                        long invDataOffset = invDataPointer - PointerInventoryEntries[i].Address;                        
                        PlayerInventory[i] = new InventoryEntry(PointerInventoryEntries[i].DerefInt(0x28), PointerInventoryEntries[i].DerefByteArray(invDataOffset + 0x10, 0x14));
                    }
                }
            }
        }
        
        public static Bitmap inventoryImage;
        public static Bitmap inventoryImagePatch1;
        public static IReadOnlyDictionary<ItemEnumeration, Rectangle> ItemToImageTranslation;
        public static IReadOnlyDictionary<Weapon, Rectangle> WeaponToImageTranslation;
        public static void GenerateImages()
        {
            // Transform the image into a 32-bit PARGB Bitmap.
            try
            {
                inventoryImage = Properties.Resources.ui0100_iam_texout.Clone(new Rectangle(0, 0, Properties.Resources.ui0100_iam_texout.Width, Properties.Resources.ui0100_iam_texout.Height), PixelFormat.Format32bppPArgb);
                inventoryImagePatch1 = Properties.Resources._40d_texout.Clone(new Rectangle(0, 0, Properties.Resources._40d_texout.Width, Properties.Resources._40d_texout.Height), PixelFormat.Format32bppPArgb);
            }
            catch (Exception ex)
            {
                Program.FailFast(string.Format("[{0}] An unhandled exception has occurred. Please see below for details.\r\n\r\n[{1}] {2}\r\n{3}.\r\n\r\nPARGB Transform.", Program.srtVersion, ex.GetType().ToString(), ex.Message, ex.StackTrace), ex);
            }

            // Rescales the image down if the scaling factor is not 1.
            if (Program.programSpecialOptions.ScalingFactor != 1d)
            {
                try
                {
                    inventoryImage = new Bitmap(inventoryImage, (int)Math.Round(inventoryImage.Width * Program.programSpecialOptions.ScalingFactor, MidpointRounding.AwayFromZero), (int)Math.Round(inventoryImage.Height * Program.programSpecialOptions.ScalingFactor, MidpointRounding.AwayFromZero));
                    inventoryImagePatch1 = new Bitmap(inventoryImagePatch1, (int)Math.Round(inventoryImagePatch1.Width * Program.programSpecialOptions.ScalingFactor, MidpointRounding.AwayFromZero), (int)Math.Round(inventoryImagePatch1.Height * Program.programSpecialOptions.ScalingFactor, MidpointRounding.AwayFromZero));
                }
                catch (Exception ex)
                {
                    Program.FailFast(string.Format(@"[{0}] An unhandled exception has occurred. Please see below for details.
---
[{1}] {2}
{3}", Program.srtVersion, ex.GetType().ToString(), ex.Message, ex.StackTrace), ex);
                }
            }

            int itemColumnInc = -1;
            int itemRowInc = -1;
            ItemToImageTranslation = new Dictionary<ItemEnumeration, Rectangle>()
            {
                { ItemEnumeration.None, new Rectangle(0, 0, 0, 0) },

                // Row 0.
                { ItemEnumeration.FirstAidSpray, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 0), Program.INV_SLOT_HEIGHT * ++itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Green2, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Red2, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Blue2, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Mixed_GG, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Mixed_GR, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Mixed_GB, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Mixed_GGB, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Mixed_GGG, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Mixed_GRB, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Mixed_RB, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Green1, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Red1, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Herb_Blue1, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },

                // Row 1.
                { ItemEnumeration.HandgunBullets, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 0), Program.INV_SLOT_HEIGHT * ++itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.ShotgunShells, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.SubmachineGunAmmo, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.MAGAmmo, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.HandgunLargeCaliberAmmo, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.SLS60HighPoweredRounds, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.GrenadeAcidRounds, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.GrenadeFlameRounds, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.NeedleCartridges, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Fuel, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.InkRibbon, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.WoodenBoard, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Gunpowder, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.GunpowderLarge, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.GunpowderHighGradeYellow, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.GunpowderHighGradeWhite, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.HipPouch, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },

                // Row 2.
                { ItemEnumeration.MatildaHighCapacityMagazine, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 0), Program.INV_SLOT_HEIGHT * ++itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.MatildaMuzzleBrake, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.MatildaGunStock, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.SLS60SpeedLoader, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.JMBHp3LaserSight, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.SLS60ReinforcedFrame, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.JMBHp3HighCapacityMagazine, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.W870ShotgunStock, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.W870LongBarrel, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.MQ11HighCapacityMagazine, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.MQ11Suppressor, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.LightningHawkRedDotSight, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.LightningHawkLongBarrel, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.GM79ShoulderStock, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.FlamethrowerRegulator, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.SparkShotHighVoltageCondenser, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },

                //Row 3.
                { ItemEnumeration.Film_HidingPlace, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 9), Program.INV_SLOT_HEIGHT * ++itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Film_RisingRookie, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Film_Commemorative, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Film_3FLocker, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Film_LionStatue, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.PortableSafe, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.TinStorageBox1, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.TinStorageBox2, new Rectangle(Program.INV_SLOT_WIDTH * itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },

                // Row 4.
                { ItemEnumeration.Detonator, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 0), Program.INV_SLOT_HEIGHT * ++itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.ElectronicGadget, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Battery9Volt, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.KeyStorageRoom, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 4), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.JackHandle, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 6), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.SquareCrank, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.MedallionUnicorn, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.KeySpade, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.KeyCardParkingGarage, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.KeyCardWeaponsLocker, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.ValveHandle, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 13), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.STARSBadge, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Scepter, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.RedJewel, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.BejeweledBox, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },

                // Row 5.
                { ItemEnumeration.PlugBishop, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 1), Program.INV_SLOT_HEIGHT * ++itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.PlugRook, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.PlugKing, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.PictureBlock, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.USBDongleKey, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.SpareKey, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.RedBook, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 8), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.StatuesLeftArm, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.StatuesLeftArmWithRedBook, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.MedallionLion, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.KeyDiamond, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.KeyCar, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.MedallionMaiden, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 15), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.PowerPanelPart1, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.PowerPanelPart2, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },

                // Row 6.
                { ItemEnumeration.LoversRelief, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 0), Program.INV_SLOT_HEIGHT * ++itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.KeyOrphanage, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.KeyClub, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.KeyHeart, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.USSDigitalVideoCassette, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.TBarValveHandle, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.SignalModulator, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.KeySewers, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 8), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.IDWristbandVisitor1, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.IDWristbandGeneralStaff1, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.IDWristbandSeniorStaff1, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.UpgradeChipGeneralStaff, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.UpgradeChipSeniorStaff, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.FuseMainHall, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 15), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Scissors, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.BoltCutter, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },

                // Row 7.
                { ItemEnumeration.StuffedDoll, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 0), Program.INV_SLOT_HEIGHT * ++itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.IDWristbandVisitor2, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 2), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.IDWristbandGeneralStaff2, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.IDWristbandSeniorStaff2, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.LabDigitalVideoCassette, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.DispersalCartridgeEmpty, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.DispersalCartridgeSolution, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.DispersalCartridgeHerbicide, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.JointPlug, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 10), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Trophy1, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 12), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.Trophy2, new Rectangle(Program.INV_SLOT_WIDTH * itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.GearSmall, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.GearLarge, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 14), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.PlugKnight, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 16), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.PlugPawn, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },

                // Row 8.
                { ItemEnumeration.PlugQueen, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 0), Program.INV_SLOT_HEIGHT * ++itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.BoxedElectronicPart1, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 2), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.BoxedElectronicPart2, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.UpgradeChipAdministrator, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 5), Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.IDWristbandAdministrator, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.KeyCourtyard, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.FuseBreakRoom, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.JointPlug2, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.GearLarge2, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },

                // Row 9.
                //     No items.

                // Row 10.
                //     No items.

                // Row 11.
                //     No items.

                // Row 12.
                //     No items.

                // Row 13.
                //     No items.

                // Row 14.
                { ItemEnumeration.WoodenBox1, new Rectangle(Program.INV_SLOT_WIDTH * (itemColumnInc = 9), Program.INV_SLOT_HEIGHT * ++itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { ItemEnumeration.WoodenBox2, new Rectangle(Program.INV_SLOT_WIDTH * ++itemColumnInc, Program.INV_SLOT_HEIGHT * itemRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },

                // Patch Items.
                { ItemEnumeration.OldKey, new Rectangle(0, 0, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
            };

            int weaponColumnInc = -1;
            int weaponRowInc = 8;
            WeaponToImageTranslation = new Dictionary<Weapon, Rectangle>()
            {
                { new Weapon() { WeaponID = WeaponEnumeration.None, Attachments = AttachmentsFlag.None }, new Rectangle(0, 0, 0, 0) },

                // Row 1.
                //     No weapons.

                // Row 2.
                //     No weapons.

                // Row 3.
                //     No weapons.

                // Row 4.
                //     No weapons.

                // Row 5.
                //     No weapons.

                // Row 6.
                //     No weapons.

                // Row 7.
                //     No weapons.

                // Row 8.
                //     No weapons.

                // Row 9.
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_Matilda, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 0), Program.INV_SLOT_HEIGHT * ++weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_Matilda, Attachments = AttachmentsFlag.First }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_Matilda, Attachments = AttachmentsFlag.Second }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 3), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_Matilda, Attachments = AttachmentsFlag.Third }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_Matilda, Attachments = AttachmentsFlag.First | AttachmentsFlag.Second }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_Matilda, Attachments = AttachmentsFlag.First | AttachmentsFlag.Third }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 7), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_Matilda, Attachments = AttachmentsFlag.Second | AttachmentsFlag.Third }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 9), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_Matilda, Attachments = AttachmentsFlag.First | AttachmentsFlag.Second | AttachmentsFlag.Third }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_JMB_Hp3, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 12), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_JMB_Hp3, Attachments = AttachmentsFlag.Second }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_JMB_Hp3, Attachments = AttachmentsFlag.Third }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_JMB_Hp3, Attachments = AttachmentsFlag.Second | AttachmentsFlag.Third }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_MUP, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_BroomHc, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },

                // Row 10.
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_SLS60, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 0), Program.INV_SLOT_HEIGHT * ++weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_SLS60, Attachments = AttachmentsFlag.Third }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH , Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_M19, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH , Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_SLS60, Attachments = AttachmentsFlag.Second }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH , Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_SLS60, Attachments = AttachmentsFlag.Second | AttachmentsFlag.Third }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Shotgun_W870, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 6), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Shotgun_W870, Attachments = AttachmentsFlag.First }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Shotgun_W870, Attachments = AttachmentsFlag.Second }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 9), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Shotgun_W870, Attachments = AttachmentsFlag.First | AttachmentsFlag.Second }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH* 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.SMG_MQ11, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 12), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.SMG_MQ11, Attachments = AttachmentsFlag.First }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH* 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.SMG_MQ11, Attachments = AttachmentsFlag.Second }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 15), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.SMG_MQ11, Attachments = AttachmentsFlag.First | AttachmentsFlag.Second }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH* 2, Program.INV_SLOT_HEIGHT) },

                // Row 11.
                { new Weapon() { WeaponID = WeaponEnumeration.SMG_LE5_Infinite2, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 0), Program.INV_SLOT_HEIGHT * ++weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_LightningHawk, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_LightningHawk, Attachments = AttachmentsFlag.First }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_LightningHawk, Attachments = AttachmentsFlag.Second }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 4), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_LightningHawk, Attachments = AttachmentsFlag.First | AttachmentsFlag.Second }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.GrenadeLauncher_GM79, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 7), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.GrenadeLauncher_GM79, Attachments = AttachmentsFlag.First }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.ChemicalFlamethrower, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 10), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.ChemicalFlamethrower, Attachments = AttachmentsFlag.Second }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 12), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.SparkShot, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 14), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.SparkShot, Attachments = AttachmentsFlag.Second }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 16), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },

                // Row 12.
                { new Weapon() { WeaponID = WeaponEnumeration.ATM4, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 0), Program.INV_SLOT_HEIGHT * ++weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.ATM4_Infinite, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 0), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.AntiTankRocketLauncher, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 2), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.AntiTankRocketLauncher_Infinite, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 2), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Minigun, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 4), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Minigun_Infinite, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 4), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.EMF_Visualizer, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 6), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_Quickdraw_Army, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.SMG_LE5_Infinite, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 9), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_SamuraiEdge_Infinite, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 11), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_SamuraiEdge_AlbertWesker, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 11), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_SamuraiEdge_ChrisRedfield, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 11), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Handgun_SamuraiEdge_JillValentine, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 11), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.ATM42, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 14), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.AntiTankRocketLauncher2, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 16), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },


                // Row 13.
                { new Weapon() { WeaponID = WeaponEnumeration.CombatKnife, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 0), Program.INV_SLOT_HEIGHT * ++weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.CombatKnife_Infinite, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH , Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Minigun2, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 2), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH * 2, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.ChemicalFlamethrower2, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 4), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.SparkShot2, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH , Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.ATM43, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH , Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.AntiTankRocketLauncher3, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH , Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.Minigun3, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH , Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.HandGrenade, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH , Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.FlashGrenade, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH , Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.ATM44, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 16), Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
                { new Weapon() { WeaponID = WeaponEnumeration.AntiTankRocketLauncher4, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * ++weaponColumnInc, Program.INV_SLOT_HEIGHT * weaponRowInc, Program.INV_SLOT_WIDTH , Program.INV_SLOT_HEIGHT) },

                // Row 14.
                { new Weapon() { WeaponID = WeaponEnumeration.Minigun4, Attachments = AttachmentsFlag.None }, new Rectangle(Program.INV_SLOT_WIDTH * (weaponColumnInc = 4), Program.INV_SLOT_HEIGHT * ++weaponRowInc, Program.INV_SLOT_WIDTH, Program.INV_SLOT_HEIGHT) },
            };
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                    if (memoryAccess != null)
                        memoryAccess.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~REmake1Memory() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}
