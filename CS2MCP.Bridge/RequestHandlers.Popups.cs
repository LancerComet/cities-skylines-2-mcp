using System;
using System.Collections;
using System.Reflection;
using Game.Settings;
using Game.UI.InGame;
using Unity.Entities;

namespace CS2MCP
{
    /// <summary>
    /// Blocking UI popups (milestone reached, signature building unlocked).
    ///
    /// While such a dialog is open the UI observes the "time.simulationPausedBarrier"
    /// event and TimeUISystem forces SimulationSystem.selectedSpeed to 0 EVERY frame,
    /// so /sim/control and /sim/run cannot resume the game until the dialog closes.
    /// The dialogs are driven by C# value bindings: clearing them closes the dialog
    /// (exactly what the dialog's own close/OK button triggers), which releases the
    /// barrier. Milestone rewards are granted before the popup is shown, so dismissing
    /// or disabling popups loses nothing.
    ///
    /// The UI types live in Colossal.UI.Binding (not referenced by this project),
    /// so bindings are accessed through reflection.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private const BindingFlags kPrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        private static object GetPrivateField(object target, string name)
        {
            return target?.GetType().GetField(name, kPrivate)?.GetValue(target);
        }

        private static object GetProp(object target, string name)
        {
            return target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
        }

        /// <summary>Entity of the milestone whose popup is showing (Entity.Null if none).</summary>
        private Entity PendingMilestonePopup()
        {
            MilestoneUISystem ui = World.GetExistingSystemManaged<MilestoneUISystem>();
            object binding = GetPrivateField(ui, "m_UnlockedMilestoneBinding");
            object value = GetProp(binding, "value");
            return value is Entity e ? e : Entity.Null;
        }

        /// <summary>Number of queued "signature building unlocked" popups.</summary>
        private int PendingSignaturePopups()
        {
            SignatureBuildingUISystem ui = World.GetExistingSystemManaged<SignatureBuildingUISystem>();
            object binding = GetPrivateField(ui, "m_UnlockSignaturesBinding");
            return GetProp(binding, "value") is ICollection list ? list.Count : 0;
        }

        /// <summary>
        /// Why the simulation is held at speed 0 by the UI: "popup" while a blocking
        /// dialog holds the pause barrier, "unfocused" while the game window lacks
        /// focus, null otherwise.
        /// </summary>
        internal string ForcedPauseReason()
        {
            try
            {
                TimeUISystem time = World.GetExistingSystemManaged<TimeUISystem>();
                if (time == null)
                {
                    return null;
                }
                object barrier = GetPrivateField(time, "m_SimulationPausedBarrierBinding");
                if (GetProp(barrier, "observerCount") is int observers && observers > 0)
                {
                    return "popup";
                }
                if (GetPrivateField(time, "m_HasFocus") is bool focus && !focus)
                {
                    return "unfocused";
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"ForcedPauseReason failed: {e.Message}");
            }
            return null;
        }

        /// <summary>
        /// Closes every pending blocking popup the same way its own button does.
        /// Returns how many were dismissed. Safe to call every frame.
        /// </summary>
        internal int DismissBlockingPopups()
        {
            int dismissed = 0;

            MilestoneUISystem milestoneUi = World.GetExistingSystemManaged<MilestoneUISystem>();
            object milestoneBinding = GetPrivateField(milestoneUi, "m_UnlockedMilestoneBinding");
            if (GetProp(milestoneBinding, "value") is Entity pending && pending != Entity.Null)
            {
                milestoneBinding.GetType().GetMethod("Update", new[] { typeof(Entity) })
                    ?.Invoke(milestoneBinding, new object[] { Entity.Null });
                dismissed++;
            }

            SignatureBuildingUISystem signatureUi = World.GetExistingSystemManaged<SignatureBuildingUISystem>();
            object signatureBinding = GetPrivateField(signatureUi, "m_UnlockSignaturesBinding");
            if (GetProp(signatureBinding, "value") is IList list && list.Count > 0)
            {
                dismissed += list.Count;
                list.Clear();
                signatureBinding.GetType().GetMethod("TriggerUpdate", Type.EmptyTypes)?.Invoke(signatureBinding, null);
            }

            return dismissed;
        }

        // GET /ui/popups
        private BridgeResponse GetPopups()
        {
            Entity milestone = PendingMilestonePopup();
            return BridgeResponse.Json(new
            {
                blockingPopupsEnabled = SharedSettings.instance?.userInterface?.blockingPopupsEnabled,
                autoDismissDuringRuns = m_System.AutoDismissPopups,
                pendingMilestonePopup = milestone == Entity.Null ? null : new { index = milestone.Index, version = milestone.Version },
                pendingSignaturePopups = PendingSignaturePopups(),
                forcedPause = ForcedPauseReason(),
                note = "a blocking popup forces the simulation to speed 0 every frame; dismiss it with /ui/popups/dismiss " +
                       "or stop them with /ui/popups/set?blocking=false (milestone rewards are granted either way)",
            });
        }

        // POST /ui/popups/dismiss
        private BridgeResponse DismissPopups()
        {
            int dismissed = DismissBlockingPopups();
            return BridgeResponse.Json(new
            {
                dismissed,
                note = dismissed > 0
                    ? "popups closed; a speed held by the popup resumes on its own, otherwise use /sim/control"
                    : "no blocking popup was pending",
            });
        }

        // POST /ui/popups/set?blocking=true|false&autoDismiss=true|false
        private BridgeResponse SetPopups(BridgeRequest request)
        {
            bool changed = false;
            bool dismissNow = false;
            if (request.TryGetBool("blocking", out bool blocking))
            {
                dismissNow |= !blocking;
                InterfaceSettings settings = SharedSettings.instance?.userInterface;
                if (settings == null)
                {
                    return BridgeResponse.Error(503, "interface settings are not available yet");
                }
                settings.blockingPopupsEnabled = blocking;
                // Persist like the options menu does (ApplyAndSave lives on the Setting base class).
                settings.GetType().GetMethod("ApplyAndSave", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                    ?.Invoke(settings, null);
                changed = true;
            }
            if (request.TryGetBool("autoDismiss", out bool autoDismiss))
            {
                m_System.AutoDismissPopups = autoDismiss;
                dismissNow |= autoDismiss;
                changed = true;
            }
            if (!changed)
            {
                return BridgeResponse.Error(400, "provide ?blocking=true|false and/or ?autoDismiss=true|false");
            }
            int dismissed = dismissNow ? DismissBlockingPopups() : 0;
            return BridgeResponse.Json(new
            {
                blockingPopupsEnabled = SharedSettings.instance?.userInterface?.blockingPopupsEnabled,
                autoDismissDuringRuns = m_System.AutoDismissPopups,
                dismissedNow = dismissed,
            });
        }
    }
}
