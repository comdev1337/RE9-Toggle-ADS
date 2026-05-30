using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

public class RE9ToggleADSProbe
{
    const int VK_RBUTTON = 0x02;
    const int VK_ESCAPE = 0x1B;
    const int VK_SHIFT = 0x10;
    const int VK_LSHIFT = 0xA0;

    static bool toggleEnabled = true;
    static bool gameHoldOverrideEnabled = true;
    static bool autoLearnHoldButton = true;
    static bool runCancelsAds = true;
    static bool diagnosticLogging;
    static bool showDebugDetails;

    static bool latched;
    static bool pressInProgress;
    static bool sawAdsDuringPress;
    static bool lastRButtonDown;
    static bool lastEscapeDown;
    static bool lastRunKeyDown;

    static int cameraMode = -1;
    static int cameraType = -1;
    static bool cameraAds;
    static int cameraHookCalls;

    static int physicalRButtonDowns;
    static int physicalRButtonUps;
    static int exitClicks;
    static int runCancelCount;
    static string lastInputAction = "none";

    static Field inputButtonOriginInputField;
    static Field inputButtonPrevOriginInputField;
    static Field inputButtonIsEnableField;
    static Field inputButtonTypeField;
    static bool inputButtonFieldsCaptured;

    static int inputButtonUpdateCalls;
    static int inputButtonPatchAttempts;
    static int inputButtonPatchForces;
    static int inputButtonPatchErrors;
    static string inputButtonPatchStatus = "not attempted";

    static ulong learnedHoldButtonAddress;
    static int learnedHoldLastSeenUpdate;
    static bool learnedHoldSeenThisPress;
    static ulong activeCandidateAddress;
    static long activeCandidateStartedAt;
    static int activeCandidateIndex = -1;
    static int learnedCandidateAttempts;
    static string candidateStatus = "not started";

    static readonly Dictionary<ulong, ButtonCandidate> buttonCandidates = new();
    static readonly List<ulong> buttonCandidateOrder = new();

    sealed class ButtonCandidate
    {
        public ulong Address;
        public int Type = -1;
        public int Seen;
        public int Forced;
        public bool Origin;
        public bool SawOriginDuringPress;
    }

    [PluginEntryPoint]
    public static void Main()
    {
        API.LogInfo("[RE9ToggleADS] loaded");
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        ReleaseLatch("unload");
        API.LogInfo("[RE9ToggleADS] unloaded");
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Post)]
    public static void OnUpdateBehaviorPost()
    {
        WatchEscape();
        WatchRunKey();
        WatchRightMouseButton();
        UpdateCandidateLearning();
    }

    [Callback(typeof(ImGuiDrawUI), CallbackType.Pre)]
    public static void OnDrawUI()
    {
        ImGui.Text("RE9 Toggle ADS (.NET)");

        if (ImGui.Checkbox("Toggle ADS enabled", ref toggleEnabled) && !toggleEnabled)
        {
            ReleaseLatch("disabled");
        }

        ImGui.Checkbox("Game hold override", ref gameHoldOverrideEnabled);
        ImGui.Checkbox("Auto-learn hold button", ref autoLearnHoldButton);
        ImGui.Checkbox("LShift cancels ADS", ref runCancelsAds);

        if (ImGui.Button("Force ADS off"))
        {
            ReleaseLatch("button");
        }

        ImGui.Text($"latched: {latched}  camera ads: {cameraAds}  mode/type: {cameraMode}/{cameraType}");
        ImGui.Text($"learned hold: 0x{learnedHoldButtonAddress:X}");
        ImGui.Text($"candidate: {candidateStatus}");
        ImGui.Text($"button patch: {inputButtonPatchStatus}");
        ImGui.Checkbox("Diagnostic logging", ref diagnosticLogging);
        ImGui.Checkbox("Show debug details", ref showDebugDetails);

        if (!showDebugDetails)
        {
            return;
        }

        ImGui.Text($"pressInProgress: {pressInProgress}");
        ImGui.Text($"sawAdsDuringPress: {sawAdsDuringPress}");
        ImGui.Text($"RMB down/up: {physicalRButtonDowns}/{physicalRButtonUps}");
        ImGui.Text($"exit clicks: {exitClicks}");
        ImGui.Text($"run cancels: {runCancelCount}");
        ImGui.Text($"last input action: {lastInputAction}");
        ImGui.Text($"camera hook calls: {cameraHookCalls}");
        ImGui.Text($"button update calls: {inputButtonUpdateCalls}");
        ImGui.Text($"button patch attempts/forces/errors: {inputButtonPatchAttempts}/{inputButtonPatchForces}/{inputButtonPatchErrors}");
        ImGui.Text($"candidate count/attempts: {buttonCandidateOrder.Count}/{learnedCandidateAttempts}");
        ImGui.Text($"active candidate: 0x{activeCandidateAddress:X}");
        ImGui.Text($"learned last seen age: {inputButtonUpdateCalls - learnedHoldLastSeenUpdate}");
        ImGui.Text($"learned seen this press: {learnedHoldSeenThisPress}");
        ImGui.Text($"button fields captured: {inputButtonFieldsCaptured}");
    }

    [MethodHook(typeof(app.PlayerCameraFOVCalc), nameof(app.PlayerCameraFOVCalc.getFOV), MethodHookType.Pre)]
    public static PreHookResult OnGetFovPre(Span<ulong> args)
    {
        cameraHookCalls++;

        try
        {
            var thisObj = ManagedObject.ToManagedObject(args[1]) as IObject;
            var idObj = thisObj?.Call("getCameraFOVID()") as IObject;
            if (idObj == null)
            {
                return PreHookResult.Continue;
            }

            cameraMode = Convert.ToInt32(idObj.Call("get_Mode()"));
            cameraType = Convert.ToInt32(idObj.Call("get_Type()"));
            cameraAds = cameraType == 1;

            if (toggleEnabled && gameHoldOverrideEnabled && pressInProgress && cameraAds)
            {
                sawAdsDuringPress = true;
                latched = true;
            }
        }
        catch (Exception e)
        {
            if (diagnosticLogging && (cameraHookCalls % 300) == 1)
            {
                API.LogWarning($"[RE9ToggleADS] camera observer failed: {e.Message}");
            }
        }

        return PreHookResult.Continue;
    }

    [MethodHook(typeof(app.InputMediator.InputButton), "update", MethodHookType.Pre)]
    public static PreHookResult OnInputButtonUpdatePre(Span<ulong> args)
    {
        return OnAnyInputButtonUpdatePre(args, "InputButton.update");
    }

    [MethodHook(typeof(app.InputMediator.InputButtonForDualSense), "update", MethodHookType.Pre)]
    public static PreHookResult OnInputButtonForDualSenseUpdatePre(Span<ulong> args)
    {
        return OnAnyInputButtonUpdatePre(args, "InputButtonForDualSense.update");
    }

    static PreHookResult OnAnyInputButtonUpdatePre(Span<ulong> args, string source)
    {
        inputButtonUpdateCalls++;

        if (args.Length <= 1)
        {
            return PreHookResult.Continue;
        }

        ulong buttonAddress = args[1];
        ObserveButtonUpdate(buttonAddress);

        if (!toggleEnabled || !gameHoldOverrideEnabled || !latched)
        {
            return PreHookResult.Continue;
        }

        return PatchInputButton(buttonAddress, source) ? PreHookResult.Skip : PreHookResult.Continue;
    }

    static void WatchRightMouseButton()
    {
        bool rButtonDown = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

        if (rButtonDown && !lastRButtonDown)
        {
            physicalRButtonDowns++;
            ResetPressCandidateFlags();
            learnedHoldSeenThisPress = false;

            if (latched)
            {
                exitClicks++;
                ReleaseLatch("rmb-down");
                lastInputAction = "exit-rmb-down";
            }
            else
            {
                pressInProgress = true;
                sawAdsDuringPress = cameraAds;
                lastInputAction = "press-rmb-down";
            }
        }
        else if (!rButtonDown && lastRButtonDown)
        {
            physicalRButtonUps++;

            if (learnedHoldButtonAddress != 0 && !learnedHoldSeenThisPress)
            {
                ClearLearnedHoldButton("learned button disappeared");
            }

            if (pressInProgress && (cameraAds || sawAdsDuringPress))
            {
                latched = true;
                lastInputAction = "latched-rmb-up";
                BeginCandidateAttemptIfNeeded();
            }

            pressInProgress = false;
            sawAdsDuringPress = false;
        }

        lastRButtonDown = rButtonDown;
    }

    static void WatchEscape()
    {
        bool escapeDown = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
        if (escapeDown && !lastEscapeDown)
        {
            ReleaseLatch("escape");
        }

        lastEscapeDown = escapeDown;
    }

    static void WatchRunKey()
    {
        bool runKeyDown = (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0 ||
            (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

        if (runCancelsAds && runKeyDown && !lastRunKeyDown && latched)
        {
            runCancelCount++;
            ReleaseLatch("run-key");
            lastInputAction = "exit-run-key";
        }

        lastRunKeyDown = runKeyDown;
    }

    static void ReleaseLatch(string reason)
    {
        if (!latched && !pressInProgress && activeCandidateAddress == 0)
        {
            return;
        }

        latched = false;
        pressInProgress = false;
        sawAdsDuringPress = false;
        activeCandidateAddress = 0;

        if (diagnosticLogging)
        {
            API.LogInfo($"[RE9ToggleADS] released latch reason={reason}");
        }
    }

    static bool PatchInputButton(ulong buttonAddress, string source)
    {
        inputButtonPatchAttempts++;

        try
        {
            if (!inputButtonFieldsCaptured)
            {
                CaptureInputButtonFields();
            }

            if (inputButtonOriginInputField == null)
            {
                inputButtonPatchStatus = "OriginInput field not found";
                return false;
            }

            ulong targetAddress = GetCurrentHoldButtonTarget();
            if (targetAddress == 0)
            {
                inputButtonPatchStatus = $"{source}: waiting for hold target";
                return false;
            }

            if (buttonAddress != targetAddress)
            {
                return false;
            }

            // This is the actual hold override: make the learned game input button look held,
            // then skip its normal update so the physical RMB release cannot clear it.
            inputButtonIsEnableField?.SetDataBoxed(buttonAddress, true, false);
            inputButtonOriginInputField.SetDataBoxed(buttonAddress, true, false);
            inputButtonPrevOriginInputField?.SetDataBoxed(buttonAddress, true, false);

            if (buttonCandidates.TryGetValue(buttonAddress, out var candidate))
            {
                candidate.Forced++;
            }

            inputButtonPatchForces++;
            inputButtonPatchStatus = $"{source}: forced 0x{buttonAddress:X}";
            return true;
        }
        catch (Exception e)
        {
            inputButtonPatchErrors++;
            inputButtonPatchStatus = $"{source}: failed {e.Message}";

            if (inputButtonPatchErrors <= 3 || diagnosticLogging)
            {
                API.LogWarning($"[RE9ToggleADS] button patch failed source={source}: {e.Message}");
            }

            return false;
        }
    }

    static ulong GetCurrentHoldButtonTarget()
    {
        if (learnedHoldButtonAddress != 0)
        {
            return learnedHoldButtonAddress;
        }

        if (autoLearnHoldButton)
        {
            return activeCandidateAddress;
        }

        return 0;
    }

    static void ObserveButtonUpdate(ulong buttonAddress)
    {
        if (buttonAddress == 0)
        {
            return;
        }

        if (buttonAddress == learnedHoldButtonAddress)
        {
            learnedHoldLastSeenUpdate = inputButtonUpdateCalls;

            if (pressInProgress || lastRButtonDown)
            {
                learnedHoldSeenThisPress = true;

                if (!inputButtonFieldsCaptured)
                {
                    CaptureInputButtonFields();
                }

                if (ReadBoolField(inputButtonOriginInputField, buttonAddress, false))
                {
                    sawAdsDuringPress = true;
                }
            }

            return;
        }

        if (learnedHoldButtonAddress != 0 || !autoLearnHoldButton)
        {
            return;
        }

        if (!pressInProgress && !lastRButtonDown)
        {
            return;
        }

        CaptureButtonCandidate(buttonAddress);
    }

    static void CaptureButtonCandidate(ulong buttonAddress)
    {
        if (buttonAddress == 0)
        {
            return;
        }

        try
        {
            if (!inputButtonFieldsCaptured)
            {
                CaptureInputButtonFields();
            }

            if (!buttonCandidates.TryGetValue(buttonAddress, out var candidate))
            {
                if (buttonCandidateOrder.Count >= 96)
                {
                    return;
                }

                candidate = new ButtonCandidate { Address = buttonAddress };
                buttonCandidates.Add(buttonAddress, candidate);
                buttonCandidateOrder.Add(buttonAddress);
                candidateStatus = $"captured {buttonCandidateOrder.Count} buttons";
            }

            candidate.Seen++;
            if (candidate.Type < 0)
            {
                candidate.Type = ReadIntField(inputButtonTypeField, buttonAddress, candidate.Type);
            }

            candidate.Origin = ReadBoolField(inputButtonOriginInputField, buttonAddress, candidate.Origin);

            // Only candidates that went active during a physical RMB press are worth testing.
            // This avoids the broad patch's fire/reload side effects while still finding the hidden ADS hold input.
            if ((pressInProgress || lastRButtonDown) && candidate.Origin)
            {
                candidate.SawOriginDuringPress = true;
            }

        }
        catch (Exception e)
        {
            if (diagnosticLogging)
            {
                API.LogWarning($"[RE9ToggleADS] candidate capture failed 0x{buttonAddress:X}: {e.Message}");
            }
        }
    }

    static void BeginCandidateAttemptIfNeeded()
    {
        if (!autoLearnHoldButton || learnedHoldButtonAddress != 0)
        {
            return;
        }

        ulong next = SelectNextCandidate();
        if (next == 0)
        {
            candidateStatus = $"waiting for candidates count={buttonCandidateOrder.Count}";
            return;
        }

        activeCandidateAddress = next;
        activeCandidateStartedAt = Environment.TickCount64;
        learnedCandidateAttempts++;
        candidateStatus = $"trying {learnedCandidateAttempts}: 0x{activeCandidateAddress:X} type={buttonCandidates[activeCandidateAddress].Type}";
    }

    static ulong SelectNextCandidate()
    {
        if (buttonCandidateOrder.Count == 0)
        {
            return 0;
        }

        for (int i = 0; i < buttonCandidateOrder.Count; i++)
        {
            activeCandidateIndex = (activeCandidateIndex + 1) % buttonCandidateOrder.Count;
            ulong address = buttonCandidateOrder[activeCandidateIndex];

            if (buttonCandidates.TryGetValue(address, out var candidate) && candidate.SawOriginDuringPress)
            {
                return address;
            }
        }

        return 0;
    }

    static void UpdateCandidateLearning()
    {
        InvalidateStaleLearnedHoldButton();

        if (!autoLearnHoldButton || learnedHoldButtonAddress != 0 || activeCandidateAddress == 0)
        {
            return;
        }

        long elapsed = Environment.TickCount64 - activeCandidateStartedAt;
        if (lastRButtonDown || elapsed < 650)
        {
            return;
        }

        if (latched && cameraAds)
        {
            learnedHoldButtonAddress = activeCandidateAddress;
            learnedHoldLastSeenUpdate = inputButtonUpdateCalls;
            candidateStatus = $"learned 0x{learnedHoldButtonAddress:X}";

            if (diagnosticLogging)
            {
                API.LogInfo($"[RE9ToggleADS] learned hold candidate 0x{learnedHoldButtonAddress:X} attempts={learnedCandidateAttempts}");
            }
        }
        else if (latched && !cameraAds)
        {
            candidateStatus = $"failed 0x{activeCandidateAddress:X}; next RMB tries another";
            activeCandidateAddress = 0;
            latched = false;
            pressInProgress = false;
            sawAdsDuringPress = false;
        }
    }

    static void ResetPressCandidateFlags()
    {
        foreach (var candidate in buttonCandidates.Values)
        {
            candidate.SawOriginDuringPress = false;
        }
    }

    static void ClearLearnedHoldButton(string reason)
    {
        if (learnedHoldButtonAddress == 0)
        {
            return;
        }

        if (diagnosticLogging)
        {
            API.LogInfo($"[RE9ToggleADS] cleared learned hold 0x{learnedHoldButtonAddress:X}: {reason}");
        }

        learnedHoldButtonAddress = 0;
        learnedHoldLastSeenUpdate = inputButtonUpdateCalls;
        activeCandidateAddress = 0;
        candidateStatus = $"{reason}; relearning";
    }

    static void InvalidateStaleLearnedHoldButton()
    {
        if (learnedHoldButtonAddress == 0)
        {
            return;
        }

        if (inputButtonUpdateCalls - learnedHoldLastSeenUpdate > 2000)
        {
            ClearLearnedHoldButton("learned button stale");
        }
    }

    static void CaptureInputButtonFields()
    {
        try
        {
            var buttonType = API.GetTDB().GetType("app.InputMediator.InputButton");
            var boolType = API.GetTDB().GetType("app.InputMediator.InputBool");

            inputButtonOriginInputField =
                buttonType?.FindField("<OriginInput>k__BackingField") ??
                buttonType?.FindField("OriginInput");

            inputButtonPrevOriginInputField =
                buttonType?.FindField("<PrevOriginInput>k__BackingField") ??
                buttonType?.FindField("PrevOriginInput");

            inputButtonTypeField = buttonType?.FindField("_Type");
            inputButtonIsEnableField =
                boolType?.FindField("<IsEnable>k__BackingField") ??
                boolType?.FindField("IsEnable");

            inputButtonFieldsCaptured = true;
            inputButtonPatchStatus =
                $"fields E={inputButtonIsEnableField != null} O={inputButtonOriginInputField != null} PO={inputButtonPrevOriginInputField != null} T={inputButtonTypeField != null}";
        }
        catch (Exception e)
        {
            inputButtonPatchStatus = $"field capture failed: {e.Message}";
        }
    }

    static int ReadIntField(Field field, ulong address, int fallback)
    {
        if (field == null || address == 0)
        {
            return fallback;
        }

        object value = field.GetDataBoxed(address, false);
        return value == null ? fallback : Convert.ToInt32(value);
    }

    static bool ReadBoolField(Field field, ulong address, bool fallback)
    {
        if (field == null || address == 0)
        {
            return fallback;
        }

        object value = field.GetDataBoxed(address, false);
        return value == null ? fallback : Convert.ToBoolean(value);
    }

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
}
