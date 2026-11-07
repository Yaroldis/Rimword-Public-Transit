using System;
using UnityEngine;
using Verse;

namespace PublicTransit.UI
{
    /// <summary>Confirmation before permanently deleting one route definition.</summary>
    public sealed class Dialog_TransitConfirm : Window
    {
        private readonly string message;
        private readonly Action onConfirm;

        public override Vector2 InitialSize => new Vector2(480f, 170f);

        public Dialog_TransitConfirm(string message, Action onConfirm)
        {
            this.message = message;
            this.onConfirm = onConfirm;
            doCloseX = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.Label(new Rect(0f, 0f, inRect.width, 70f), message);
            float y = inRect.height - 38f;
            if (Widgets.ButtonText(new Rect(inRect.width - 250f, y, 115f, 32f), "PT_Cancel".Translate()))
                Close();
            if (Widgets.ButtonText(new Rect(inRect.width - 125f, y, 125f, 32f), "PT_Delete".Translate()))
            {
                onConfirm?.Invoke();
                Close();
            }
        }
    }
}
