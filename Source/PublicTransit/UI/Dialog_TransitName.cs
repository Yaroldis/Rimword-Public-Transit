using System;
using UnityEngine;
using Verse;

namespace PublicTransit.UI
{
    /// <summary>Shared small modal for naming a stop or a route.</summary>
    public sealed class Dialog_TransitName : Window
    {
        private readonly string title;
        private readonly Action<string> onAccept;
        private string currentValue;

        public override Vector2 InitialSize => new Vector2(460f, 180f);

        public Dialog_TransitName(string title, string initialValue, Action<string> onAccept)
        {
            this.title = title;
            this.onAccept = onAccept;
            currentValue = initialValue ?? string.Empty;
            doCloseX = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.Label(new Rect(0f, 0f, inRect.width, 28f), title);
            currentValue = Widgets.TextField(new Rect(0f, 38f, inRect.width, 34f), currentValue);
            if (currentValue.Length > 48) currentValue = currentValue.Substring(0, 48);

            float buttonWidth = 115f;
            float y = inRect.height - 38f;
            if (Widgets.ButtonText(new Rect(inRect.width - buttonWidth * 2 - 8f, y, buttonWidth, 32f),
                "PT_Cancel".Translate()))
            {
                Close();
            }

            if (Widgets.ButtonText(new Rect(inRect.width - buttonWidth, y, buttonWidth, 32f),
                "PT_Save".Translate()) && !string.IsNullOrWhiteSpace(currentValue))
            {
                onAccept?.Invoke(currentValue.Trim());
                Close();
            }
        }
    }
}
