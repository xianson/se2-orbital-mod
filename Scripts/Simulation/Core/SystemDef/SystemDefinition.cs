using System;
using System.Collections.Generic;

namespace SEAerospace.SystemDef
{
    /// <summary>
    /// A whole star system as data: a name, an epoch, and a flat list of
    /// <see cref="BodyDefinition"/> linked by parent name. ONE XML document describes the
    /// entire solar system — star, planets, moons, gas giants — by real orbital elements.
    /// This is the clean replacement for RSS's config, fixing its documented flaws (see
    /// <see cref="BodyDefinition"/>); in particular there is no per-body hijacked string
    /// field and no free-floating period.
    ///
    /// SERIALIZATION SEAM (why the hooks): in-game, the SE mod whitelist PROHIBITS
    /// <c>System.Xml.Serialization.XmlSerializer</c> and <c>StringReader/StringWriter</c>;
    /// the only allowed path is <c>MyAPIGateway.Utilities.SerializeToXML/FromXML</c>, which
    /// is game-only and therefore unavailable in the offline harness. To keep this model
    /// class both whitelist-clean (no prohibited types here) AND unit-testable offline, the
    /// actual (de)serialization is delegated to two pluggable hooks:
    ///   - In-game: <see cref="SystemDefStorage.Install"/> wires them to MyAPIGateway.
    ///   - Offline: the test installs a plain <see cref="System.Xml.Serialization.XmlSerializer"/>
    ///     backend (legal outside the game) before exercising the model.
    /// The XML shape is identical either way (same [XmlRoot]/[XmlArray] attributes).
    /// </summary>
    public class SystemDefinition
    {
        /// <summary>Display name of the system (e.g. "Sol").</summary>
        public string Name = "";

        /// <summary>System epoch in seconds — the time at which every body's
        /// MeanAnomalyAtEpoch holds. Mean anomalies advance from here.</summary>
        public double EpochSeconds = 0.0;

        /// <summary>All bodies, flat. Parent links are by <see cref="BodyDefinition.Parent"/>
        /// name; the builder resolves them into the tree.</summary>
        public List<BodyDefinition> Bodies = new List<BodyDefinition>();

        public SystemDefinition() { }

        // ---- pluggable XML backend (whitelist-clean here; installed by host) ----

        /// <summary>Serialize a SystemDefinition to an XML string. Installed by the host
        /// (game: MyAPIGateway; tests: plain XmlSerializer).</summary>
        public static Func<SystemDefinition, string> XmlSerializeHook;

        /// <summary>Parse a SystemDefinition from an XML string. Installed by the host.</summary>
        public static Func<string, SystemDefinition> XmlDeserializeHook;

        /// <summary>Serialize to XML using the installed backend.</summary>
        public string ToXml()
        {
            if (XmlSerializeHook == null)
                throw new InvalidOperationException(
                    "SystemDefinition XML backend not installed (call SystemDefStorage.Install in-game, or install a test backend offline).");
            return XmlSerializeHook(this);
        }

        /// <summary>Parse from XML using the installed backend.</summary>
        public static SystemDefinition FromXml(string xml)
        {
            if (XmlDeserializeHook == null)
                throw new InvalidOperationException(
                    "SystemDefinition XML backend not installed (call SystemDefStorage.Install in-game, or install a test backend offline).");
            return XmlDeserializeHook(xml);
        }
    }
}
