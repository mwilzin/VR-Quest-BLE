using System;
using System.Collections.Generic;

namespace Teamprojekt.Ftms
{
    public static class FtmsSelection
    {
        public static FtmsDevice ChooseBike(IReadOnlyList<FtmsDevice> found, string nameFilter)
        {
            var pool = Filter(found, nameFilter, string.IsNullOrEmpty(nameFilter));
            return PreferIndoor(pool);
        }

        public static FtmsDevice ChooseHeart(IReadOnlyList<FtmsDevice> found, string nameFilter, string bikeAddress)
        {
            List<FtmsDevice> pool;
            if (!string.IsNullOrEmpty(nameFilter))
            {
                pool = Filter(found, nameFilter, false);
            }
            else
            {
                var hrOnly = new List<FtmsDevice>();
                var anyHr = new List<FtmsDevice>();
                for (int i = 0; i < found.Count; i++)
                {
                    if (!found[i].IsHr)
                        continue;
                    anyHr.Add(found[i]);
                    if (!found[i].IsFtms)
                        hrOnly.Add(found[i]);
                }
                pool = hrOnly.Count > 0 ? hrOnly : anyHr;
            }
            FtmsDevice chosen = PreferIndoor(pool);
            if (chosen != null && SameAddress(chosen.Address, bikeAddress))
                return null;
            return chosen;
        }

        public static bool SameAddress(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                return false;
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        static FtmsDevice PreferIndoor(List<FtmsDevice> pool)
        {
            if (pool == null || pool.Count == 0)
                return null;
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i].MachineTypes != null && pool[i].MachineTypes.Contains("indoor bike"))
                    return pool[i];
            }
            return pool[0];
        }

        static List<FtmsDevice> Filter(IReadOnlyList<FtmsDevice> found, string nameFilter, bool ftmsOnly)
        {
            var pool = new List<FtmsDevice>();
            if (found == null)
                return pool;
            for (int i = 0; i < found.Count; i++)
            {
                FtmsDevice device = found[i];
                if (ftmsOnly && !device.IsFtms)
                    continue;
                if (!string.IsNullOrEmpty(nameFilter) && !Matches(device, nameFilter))
                    continue;
                pool.Add(device);
            }
            return pool;
        }

        static bool Matches(FtmsDevice device, string query)
        {
            string needle = query.ToLowerInvariant();
            string name = device.Name ?? "";
            string address = device.Address ?? "";
            return name.ToLowerInvariant().Contains(needle) || address.ToLowerInvariant().Contains(needle);
        }
    }
}
