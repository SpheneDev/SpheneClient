using CheapLoc;

namespace Sphene.Localization;

public static class Strings
{
    public static class ToS
    {
        public static string LanguageLabel => Loc.Localize("ToS.LanguageLabel", "Language");
        public static string AgreementLabel => Loc.Localize("ToS.AgreementLabel", "     Terms of Service");
        public static string ReadLabel => Loc.Localize("ToS.ReadLabel", "Please read carefully before proceeding");
        public static string AgreeLabel => Loc.Localize("ToS.AgreeLabel", "I Accept & Continue");
        public static string ButtonWillBeAvailableIn => Loc.Localize("ToS.ButtonWillBeAvailableIn", "Continue button available in");

        public static string Paragraph1 => Loc.Localize("ToS.Paragraph1",
            "Welcome to the Sphene Network - a revolutionary character synchronization service for Final Fantasy XIV. " +
            "By using this service, you acknowledge that you understand and accept the following terms and conditions.");

        public static string Paragraph2 => Loc.Localize("ToS.Paragraph2",
            "The Sphene Network is provided as-is without any warranties or guarantees. While we strive to maintain " +
            "reliable service, we cannot guarantee 100% uptime or data integrity. Use this service at your own discretion " +
            "and always maintain local backups of important character data.");

        public static string Paragraph3 => Loc.Localize("ToS.Paragraph3",
            "Your privacy is important to us. Character appearance data transmitted through the Network is encrypted " +
            "and only shared with users you explicitly authorize. We do not collect, store, or share personal information " +
            "beyond what is necessary for service operation.");

        public static string Paragraph4 => Loc.Localize("ToS.Paragraph4",
            "You are responsible for ensuring that any mods or customizations you share comply with Final Fantasy XIV's " +
            "Terms of Service and applicable laws. The Sphene Network is not responsible for content shared by users. " +
            "Inappropriate, illegal, or harmful content is strictly prohibited and may result in service suspension.");

        public static string Paragraph5 => Loc.Localize("ToS.Paragraph5",
            "The Network requires Penumbra and Glamourer plugins to function properly. Only modifications channeled " +
            "through Penumbra can be synchronized. Ensure all your customizations are properly configured through " +
            "Penumbra's systems for optimal synchronization results.");

        public static string Paragraph6 => Loc.Localize("ToS.Paragraph6",
            "By continuing, you agree to these terms and acknowledge that the Sphene Network is a community-driven " +
            "project provided free of charge. We reserve the right to modify these terms or discontinue the service " +
            "at any time. Thank you for being part of the Sphene Network community!");
    }
}
