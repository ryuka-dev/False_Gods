// Unity / game UI interop (none of those APIs carry nullable annotations), so this file opts out of the
// nullable-reference context like the other game-facing implementations.
#nullable disable

using System;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.DevTools;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Arena
{
    /// <summary>
    /// A row in the game's own developer menu that goes straight to the arena.
    /// </summary>
    /// <remarks>
    /// <para><b>For developing, not for playing.</b> Ordinary play reaches the arena by beating the cave boss and
    /// walking through what opens in its room; this is the shortcut for the twentieth time you need to look at
    /// something in there. It lives behind the game's own developer mode, which is off for players, so it is not
    /// a control that has to be taken away before release.</para>
    /// <para><b>An existing row is copied rather than a new one built.</b> The menu's own level buttons are
    /// instantiated from a prefab held privately by each chapter panel, and they carry that menu's font, colours,
    /// hover behaviour and layout. Copying one that is already standing gets all of it and leaves nothing to
    /// drift out of step when the menu is restyled — the same reason this project borrows rooms rather than
    /// modelling them.</para>
    /// <para><b>Added where the chapter it belongs to is.</b> The arena stands in as the first cave level, so its
    /// row goes at the end of the panel that lists the caves — beside the level it replaces rather than in a
    /// section of its own.</para>
    /// </remarks>
    public sealed class SulfurDevMenuEntry
    {
        /// <summary>What the row says. Prefixed like the menu's own rows, which read "&lt;environment&gt; N: name".
        /// </summary>
        private const string Label = "FALSE GODS: boss arena";

        /// <summary>The chapter panel to hang it on: the one whose rows mention the caves, since the arena stands
        /// in as a cave level. Matched loosely — a menu that names its acts differently gets the first panel
        /// rather than no button.</summary>
        private const string PreferredChapter = "Caves";

        private readonly Action _goToTheArena;
        private readonly ILogger _logger;

        private GameObject _row;

        public SulfurDevMenuEntry(Action goToTheArena, ILogger logger = null)
        {
            _goToTheArena = goToTheArena ?? throw new ArgumentNullException(nameof(goToTheArena));
            _logger = logger;
        }

        /// <summary>
        /// Put the row in once the menu has built itself, and again if the menu is ever rebuilt.
        /// </summary>
        /// <remarks>
        /// <para>Cheap to call every frame: once the row exists this is a field read, and until the developer menu
        /// is actually open it is two static reads.</para>
        /// <para><b>Why the open check is not optional.</b> The scene query below is
        /// <c>FindObjectsByType(FindObjectsInactive.Include)</c>, which walks every loaded object — and the menu
        /// only builds the panels it looks for the first time it is shown. Without a gate, a session that never
        /// opens the menu (every session a player has: the menu needs developer mode) runs that walk on every
        /// frame forever. Measured 2026-08-03: this and the cave-boss query together cost about half the frame
        /// rate of an ordinary level. The menu's own state is the canonical answer to "is there anything to look
        /// for yet", and it is what the game itself tests (<c>GameManager</c> gates its developer input the same
        /// way), so this needs no observer and no timer.</para>
        /// </remarks>
        public void Maintain()
        {
            if (_row != null)
            {
                return; // still standing; a destroyed one compares equal to null and sends this looking again
            }

            if (!TheMenuIsOpen())
            {
                return; // nothing has built a level list to copy a row from, and nothing is there to see it
            }

            ChapterPanel[] panels;
            try
            {
                panels = UnityEngine.Object.FindObjectsByType<ChapterPanel>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[dev-menu] could not look for the level list ({exception.Message}).");
                return;
            }

            if (panels == null || panels.Length == 0)
            {
                return; // the menu has not been opened yet, so it has not built its list
            }

            var panel = PickPanel(panels);
            var template = FindARow(panel);
            if (template == null)
            {
                return; // the panel is there but empty; its levels load asynchronously
            }

            try
            {
                _row = UnityEngine.Object.Instantiate(template.gameObject, panel.transform);
                _row.name = "FalseGodsArenaRow";
                _row.transform.SetAsLastSibling();

                var text = _row.GetComponentInChildren<TextMeshProUGUI>();
                if (text != null)
                {
                    text.text = Label;
                }

                var button = _row.GetComponent<Button>();
                if (button == null)
                {
                    _logger?.LogWarning("[dev-menu] the copied row has no button; no arena row was added.");
                    UnityEngine.Object.Destroy(_row);
                    _row = null;
                    return;
                }

                // The copy came with the listener that takes you to whatever level it was for.
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(GoThere);
                _logger?.Log($"[dev-menu] an arena row was added to '{panel.name}'.");
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[dev-menu] the arena row could not be added ({exception.Message}).");
                _row = null;
            }
        }

        /// <summary>Whether the developer menu is up right now, which is also the only time it has a level list.
        /// </summary>
        /// <remarks>Two static reads and a bool. Developer mode is checked first because it is the cheaper of the
        /// two and the one that is false for every player: with it off the menu cannot be opened at all, so the
        /// row is never needed. Any throw is treated as "not open" — a row that fails to appear costs a developer
        /// one shortcut, while a throw here would cost everyone the frame.</remarks>
        private bool TheMenuIsOpen()
        {
            try
            {
                if (!GameManager.DeveloperMode)
                {
                    return false;
                }

                var menu = StaticInstance<DevToolsManager>.Instance;
                return menu != null && menu.shouldShow;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[dev-menu] could not tell whether the menu is open ({exception.Message}).");
                return false;
            }
        }

        /// <summary>Close the menu first, exactly as the menu's own rows do — leaving it open over a level that is
        /// being torn down is the game's own reason for doing that, not ours.</summary>
        private void GoThere()
        {
            try
            {
                var menu = DevToolsManager.Instance;
                if (menu != null)
                {
                    menu.TurnOff();
                }
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[dev-menu] the menu would not close ({exception.Message}); going anyway.");
            }

            _goToTheArena();
        }

        private static ChapterPanel PickPanel(ChapterPanel[] panels)
        {
            for (var i = 0; i < panels.Length; i++)
            {
                if (panels[i] != null && panels[i].name.IndexOf(PreferredChapter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return panels[i];
                }
            }

            // Or the first one that has rows to copy — a button in the wrong chapter beats no button.
            for (var i = 0; i < panels.Length; i++)
            {
                if (panels[i] != null && FindARow(panels[i]) != null)
                {
                    return panels[i];
                }
            }

            return panels[0];
        }

        /// <summary>One of the panel's own level rows, to copy. Its own children only — the header is not a row.
        /// </summary>
        private static Button FindARow(ChapterPanel panel)
        {
            if (panel == null)
            {
                return null;
            }

            foreach (Transform child in panel.transform)
            {
                var button = child.GetComponent<Button>();
                if (button != null)
                {
                    return button;
                }
            }

            return null;
        }
    }
}
