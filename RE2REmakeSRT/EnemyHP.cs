using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace RE2REmakeSRT
{
    public readonly struct EnemyHP
    {
        public readonly int MaximumHP;
        public readonly int CurrentHP;
        public readonly bool IsAlive;
        public readonly float Percentage;

        public EnemyHP(int maximumHP, int currentHP)
        {
            MaximumHP = maximumHP;
            CurrentHP = (currentHP <= maximumHP) ? currentHP : 0;
            IsAlive = MaximumHP > 0 && CurrentHP > 0 && CurrentHP <= MaximumHP;
            Percentage = (IsAlive) ? (float)CurrentHP / (float)MaximumHP : 0f;
        }
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Ansi)]
    public struct RE2_PoisonInfo
    {
        [FieldOffset(0x258)]
        public bool IsPoisoned;

        public RE2_PoisonInfo(bool poisoned)
        {
            IsPoisoned = poisoned;
        }
    }
 
    

    public struct RE2_NPC
    {
        public int MaximumHP;
        public int CurrentHP;
        public bool IsPoisoned;
        public float PosX;
        public float PosY;
        public float PosZ;

        public bool IsAlive()
        {
            return MaximumHP > 0 && CurrentHP > 0 && CurrentHP <= MaximumHP;
        }        

        public float Percentage()
        {
            return (MaximumHP > 0 && CurrentHP > 0 && CurrentHP <= MaximumHP) ? (float)CurrentHP / (float)MaximumHP : 0f;
        }


        public void Update(int maximumHP, int currentHP, bool poisoned)
        {
            PosX = 0f;
            PosY = 0f;
            PosZ = 0f;
            MaximumHP = maximumHP;
            CurrentHP = (currentHP <= maximumHP) ? currentHP : 0;
            IsPoisoned = poisoned;
        }

        public RE2_NPC(int maximumHP, int currentHP, bool poisoned)
        {
            PosX = 0f;
            PosY = 0f;
            PosZ = 0f;
            MaximumHP = maximumHP;
            CurrentHP = (currentHP <= maximumHP) ? currentHP : 0;
            IsPoisoned = poisoned;
        }        
    }

}

