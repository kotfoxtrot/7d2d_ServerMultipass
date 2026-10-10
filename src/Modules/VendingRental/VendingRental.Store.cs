using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using Newtonsoft.Json;

namespace ServerMultipass.Modules
{
    public sealed class VendingRent
    {
        public int X;
        public int Y;
        public int Z;
        public string Owner;
        public string Name;
        public int EndDay;
        public int Till;
        public int Charged;
        public int WarnedDay;
        public ulong LastPurchase;
        public ulong NextAutoBuy;
        public bool Player;

        [JsonIgnore]
        public Vector3i Pos
        {
            get => new Vector3i(X, Y, Z);
            set
            {
                X = value.x;
                Y = value.y;
                Z = value.z;
            }
        }
    }

    public sealed class VendingRentalData
    {
        public List<VendingRent> Rentals = new List<VendingRent>();
        public List<VendingRent> Stored = new List<VendingRent>();
    }

    public sealed partial class VendingRental
    {
        private const string DataFile = "VendingRental.json";
        private const string LegacyFile = "VendingRental.xml";

        private readonly Dictionary<Vector3i, VendingRent> rentals = new Dictionary<Vector3i, VendingRent>();
        private readonly List<VendingRent> stored = new List<VendingRent>();
        private bool dirty;
        private bool loaded;

        private void LoadData()
        {
            loaded = false;
            dirty = false;
            rentals.Clear();
            stored.Clear();
            var data = File.Exists(Path.Combine(Multipass.WorldPath, DataFile))
                ? LoadWorld<VendingRentalData>(DataFile)
                : ImportLegacy();
            if (data != null)
            {
                foreach (var rental in data.Rentals.Where(r => !string.IsNullOrEmpty(r?.Owner))) rentals[rental.Pos] = rental;
                stored.AddRange(data.Stored.Where(r => !string.IsNullOrEmpty(r?.Owner)));
            }
            loaded = true;
        }

        private void SaveData()
        {
            if (!loaded) return;
            SaveWorld(DataFile, new VendingRentalData { Rentals = rentals.Values.ToList(), Stored = stored.ToList() });
            dirty = false;
        }

        private void RemoveRental(Vector3i pos)
        {
            if (rentals.Remove(pos)) dirty = true;
        }

        private int CountActive(string ownerId, int today)
        {
            return rentals.Values.Count(r => r.Owner == ownerId && r.EndDay > today)
                   + stored.Count(r => r.Owner == ownerId && r.EndDay > today);
        }

        private VendingRentalData ImportLegacy()
        {
            var path = Path.Combine(GameIO.GetSaveGameDir(), LegacyFile);
            if (!File.Exists(path)) return null;
            var document = new XmlDocument();
            document.Load(path);
            var data = new VendingRentalData();
            foreach (XmlNode node in document.DocumentElement.ChildNodes)
            {
                if (!(node is XmlElement element) || string.IsNullOrEmpty(element.GetAttribute("owner"))) continue;
                if (element.Name != "rental" && element.Name != "stored") continue;
                var rental = new VendingRent
                {
                    X = Int(element, "x"),
                    Y = Int(element, "y"),
                    Z = Int(element, "z"),
                    Owner = element.GetAttribute("owner"),
                    Name = element.GetAttribute("name"),
                    EndDay = Int(element, "end"),
                    Till = Int(element, "till"),
                    Charged = Int(element, "charged"),
                    WarnedDay = Int(element, "warned"),
                    LastPurchase = Long(element, "lastPurchaseTime"),
                    NextAutoBuy = Long(element, "nextAutoBuyTime"),
                    Player = element.GetAttribute("player") == "true"
                };
                (element.Name == "stored" ? data.Stored : data.Rentals).Add(rental);
            }
            SaveWorld(DataFile, data);
            Info($"{data.Rentals.Count} rentals and {data.Stored.Count} stored rentals taken over from {path}, that file is no longer used");
            return data;
        }

        private static int Int(XmlElement element, string name)
        {
            return int.TryParse(element.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }

        private static ulong Long(XmlElement element, string name)
        {
            return ulong.TryParse(element.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0UL;
        }
    }
}
