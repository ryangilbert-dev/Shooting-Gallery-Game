using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using ShootingGallery.Gameplay;

/// <summary>
/// Wires up the walk cycle each character in the PSX-Western pack already ships (in a separate
/// "{Name}Anim.fbx" per character) but that nothing in this project used to play.
///
/// The two FBX files use completely different skeletons: the base model ("{Name}.fbx") is rigged
/// with Reallusion's CC_Base_* bone names, while the animation file uses Mixamo's mixamorig:*
/// names (it was very likely animated by running the base model through Mixamo's auto-rigger).
/// A Generic-type Animator can only ever play a clip on the exact skeleton it was authored for, so
/// there's no way to play a mixamorig clip directly on a CC_Base mesh - the fix is Humanoid
/// retargeting: both FBX files get their own Humanoid Avatar, which bakes the animation into
/// skeleton-independent data that then plays back correctly on the *other* rig's Avatar.
///
/// The mixamorig:* animation file's avatar is left to Unity's own auto-mapper (a very
/// well-supported convention it gets right reliably). The CC_Base_* base model's avatar is NOT -
/// first pass through this tool auto-mapped it, and playtesting immediately showed why that's not
/// trustworthy for this rig: characters walked on all fours (arms driving as legs) and sank into
/// the ground (both point at leg/hip bones having landed on the wrong Humanoid slots, which also
/// throws off the hip-height scaling retargeting depends on). Fixed by building an explicit,
/// hand-verified bone mapping instead (see BuildCCBaseHumanBones) rather than trusting
/// auto-detection at all for that rig.
///
/// Logs a clear OK/warning per character - a warning means that character's Humanoid avatar
/// failed to build (most likely its rig uses different bone names than the rest of the pack, since
/// BuildCCBaseHumanBones assumes they're all identical) and needs a manual look in the Editor
/// (select the FBX, Rig tab, Configure Avatar, fix whatever's flagged red). Safe to re-run - Setup
/// wipes and rebuilds ControllerOutputFolder from scratch every time specifically so re-running is
/// actually safe: AnimatorController.CreateAnimatorControllerAtPath silently renames instead of
/// overwriting when something already exists at its target path, so without the wipe, repeated
/// runs accumulate orphaned duplicate controller files - confirmed to eventually leave a
/// previously-wired prefab's Animator pointing at a "Missing" controller once that churn caught up
/// with it. Don't remove the wipe without solving that differently.
///
/// If characters still look wrong after this fix (an OK log doesn't guarantee every slot landed
/// exactly right, only that the required ones did), the "Neck" mapping is the shakiest guess in
/// BuildCCBaseHumanBones - this rig has no bone plainly named "Neck", only NeckTwist01/02, and
/// NeckTwist01 was picked as the closer analog without being able to see it in motion.
///
/// Known rough edge this doesn't address: PlayerWeapon's arm-raise pose (drawn revolver) writes to
/// the same arm bones a walk cycle drives, so the two can visually fight if you walk while aiming.
/// Fixing that cleanly needs an upper/lower-body AvatarMask split - left for later if it turns out
/// to actually bother anyone in testing.
/// </summary>
public static class CharacterAnimationSetup
{
    private const string CharactersRoot = "Assets/Art/PSX-Western Characters Pack/Characters";
    private const string ControllerOutputFolder = "Assets/Animations";
    private const string PrefabFolder = "Assets/Prefabs/Characters";

    private static readonly string[] CharacterNames =
    {
        "Cowboy1", "Cowboy2", "Cowboy3", "Cowboy4",
        "EliteCowboy1", "EliteCowboy2", "BountyHunter", "Woman",
    };

    [MenuItem("Tools/Shooting Gallery/Setup Character Walk Animation")]
    public static void Setup()
    {
        // Wipe and rebuild this whole folder from scratch every run, rather than trying to
        // overwrite individual controllers in place. AssetDatabase.CreateAsset (used internally
        // by AnimatorController.CreateAnimatorControllerAtPath below) silently renames the new
        // asset instead of overwriting when something already exists at that path - re-running
        // this tool without a clean slate accumulates orphaned duplicate controller files
        // ("Locomotion 1.controller", "2.controller", ...), and was confirmed to actually leave a
        // previously-wired character prefab's Animator pointing at a "Missing" controller after a
        // few re-runs. A full wipe is simpler and more robust than per-asset overwrite logic.
        if (AssetDatabase.IsValidFolder(ControllerOutputFolder))
        {
            AssetDatabase.DeleteAsset(ControllerOutputFolder);
        }

        AssetDatabase.CreateFolder("Assets", "Animations");

        int succeeded = 0;
        foreach (string name in CharacterNames)
        {
            if (SetupOneCharacter(name))
            {
                succeeded++;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[CharacterAnimationSetup] Wired walk animation for {succeeded}/{CharacterNames.Length} " +
                  "characters using an explicit CC_Base_* bone mapping (not Unity's auto-mapper - see this " +
                  "file's doc comment for why). Any warnings above mean that character's rig didn't match the " +
                  "assumed bone names - select its FBX, open the Rig tab, and check Configure Avatar.");
    }

    private static bool SetupOneCharacter(string name)
    {
        string baseFbxPath = $"{CharactersRoot}/{name}/{name}.fbx";
        string animFbxPath = $"{CharactersRoot}/{name}/{name}Anim.fbx";
        string prefabPath = $"{PrefabFolder}/{name}.prefab";

        var baseImporter = AssetImporter.GetAtPath(baseFbxPath) as ModelImporter;
        var animImporter = AssetImporter.GetAtPath(animFbxPath) as ModelImporter;
        if (baseImporter == null || animImporter == null)
        {
            Debug.LogWarning($"[CharacterAnimationSetup] {name}: missing base or anim FBX - skipped.");
            return false;
        }

        // Base model: gets its own Humanoid avatar (CC_Base_* rig) - this is what the Animator
        // actually drives at runtime. First pass just to get Unity to populate .skeleton (the
        // bone hierarchy/bind poses - unaffected by the bug below, so safe to keep as-is).
        baseImporter.animationType = ModelImporterAnimationType.Human;
        baseImporter.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        baseImporter.SaveAndReimport();

        // Unity's own name-heuristic auto-mapper gets this rig's bones wrong - confirmed by
        // playtesting: characters ended up walking on all fours (arms driving as legs) and sunk
        // into the ground (both classic symptoms of leg/hip bones landing on the wrong Humanoid
        // slots, which also throws off the hip-height scaling retargeting relies on). Replace
        // just the .human mapping with an explicit, hand-verified one built from the actual
        // CC_Base_* bone list (see BuildCCBaseHumanBones) - keeps the .skeleton Unity just
        // auto-populated, only the slot assignment changes.
        HumanDescription description = baseImporter.humanDescription;
        description.human = BuildCCBaseHumanBones();
        baseImporter.humanDescription = description;
        baseImporter.SaveAndReimport();

        // Anim file: also gets its own Humanoid avatar (mixamorig:* rig) - purely so its clip gets
        // baked into abstract muscle-space data, which is what makes it retargetable onto the
        // base model's differently-named rig at all.
        animImporter.animationType = ModelImporterAnimationType.Human;
        animImporter.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        animImporter.SaveAndReimport();

        // Explicitly name and loop the single take - a raw auto-generated clip defaults to
        // "loop time" off, so a walk cycle would play once and freeze on the last frame instead
        // of cycling continuously while the player keeps moving.
        if (animImporter.importedTakeInfos.Length > 0)
        {
            var take = animImporter.importedTakeInfos[0];
            animImporter.clipAnimations = new[]
            {
                new ModelImporterClipAnimation
                {
                    name = "Walk",
                    takeName = take.name,
                    firstFrame = take.startTime * take.sampleRate,
                    lastFrame = take.stopTime * take.sampleRate,
                    loopTime = true,
                    loopPose = true,
                }
            };
            animImporter.SaveAndReimport();
        }

        Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(baseFbxPath).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isValid || !avatar.isHuman)
        {
            Debug.LogWarning($"[CharacterAnimationSetup] {name}: base model's Humanoid avatar failed to build " +
                              "from the explicit CC_Base_* mapping - skipped. This character's rig likely uses " +
                              "different bone names than the others (BuildCCBaseHumanBones assumes they're all " +
                              "identical) - check its FBX's Rig tab for which required bone is missing.");
            return false;
        }

        Avatar animAvatar = AssetDatabase.LoadAllAssetsAtPath(animFbxPath).OfType<Avatar>().FirstOrDefault();
        if (animAvatar == null || !animAvatar.isValid || !animAvatar.isHuman)
        {
            Debug.LogWarning($"[CharacterAnimationSetup] {name}: animation file's Humanoid avatar failed to " +
                              "auto-map from its mixamorig:* rig - skipped. Fix in its FBX's Rig tab.");
            return false;
        }

        AnimationClip walkClip = AssetDatabase.LoadAllAssetsAtPath(animFbxPath)
            .OfType<AnimationClip>()
            .FirstOrDefault(c => !c.name.Contains("__preview__"));
        if (walkClip == null)
        {
            Debug.LogWarning($"[CharacterAnimationSetup] {name}: no animation clip found in {animFbxPath} - skipped.");
            return false;
        }

        AnimatorController controller = BuildWalkIdleController(name, walkClip);
        WireIntoCharacterPrefab(prefabPath, controller, avatar);

        Debug.Log($"[CharacterAnimationSetup] {name}: OK - '{walkClip.name}' wired as the Walk state.");
        return true;
    }

    /// <summary>
    /// Explicit Humanoid bone mapping for this pack's shared CC_Base_* skeleton (Reallusion
    /// Character Creator naming), hand-verified against the actual bone list pulled from
    /// Cowboy1.fbx - Unity's own auto-mapper gets this rig wrong (see the caller). All 8
    /// characters in the pack share this same skeleton, so one mapping covers all of them.
    /// Twist/share/secondary bones (ThighTwist01, KneeShareBone, RibsTwist, etc.) are
    /// deliberately left unmapped - they're corrective deformation bones, not the primary chain.
    /// </summary>
    private static HumanBone[] BuildCCBaseHumanBones()
    {
        (string human, string bone)[] map =
        {
            ("Hips", "CC_Base_Hip"),
            ("Spine", "CC_Base_Waist"),
            ("Chest", "CC_Base_Spine01"),
            ("UpperChest", "CC_Base_Spine02"),
            ("Neck", "CC_Base_NeckTwist01"),
            ("Head", "CC_Base_Head"),
            ("LeftEye", "CC_Base_L_Eye"),
            ("RightEye", "CC_Base_R_Eye"),
            ("Jaw", "CC_Base_JawRoot"),

            ("LeftShoulder", "CC_Base_L_Clavicle"),
            ("LeftUpperArm", "CC_Base_L_Upperarm"),
            ("LeftLowerArm", "CC_Base_L_Forearm"),
            ("LeftHand", "CC_Base_L_Hand"),
            ("RightShoulder", "CC_Base_R_Clavicle"),
            ("RightUpperArm", "CC_Base_R_Upperarm"),
            ("RightLowerArm", "CC_Base_R_Forearm"),
            ("RightHand", "CC_Base_R_Hand"),

            ("LeftUpperLeg", "CC_Base_L_Thigh"),
            ("LeftLowerLeg", "CC_Base_L_Calf"),
            ("LeftFoot", "CC_Base_L_Foot"),
            ("LeftToes", "CC_Base_L_ToeBase"),
            ("RightUpperLeg", "CC_Base_R_Thigh"),
            ("RightLowerLeg", "CC_Base_R_Calf"),
            ("RightFoot", "CC_Base_R_Foot"),
            ("RightToes", "CC_Base_R_ToeBase"),

            ("LeftThumbProximal", "CC_Base_L_Thumb1"),
            ("LeftThumbIntermediate", "CC_Base_L_Thumb2"),
            ("LeftThumbDistal", "CC_Base_L_Thumb3"),
            ("LeftIndexProximal", "CC_Base_L_Index1"),
            ("LeftIndexIntermediate", "CC_Base_L_Index2"),
            ("LeftIndexDistal", "CC_Base_L_Index3"),
            ("LeftMiddleProximal", "CC_Base_L_Mid1"),
            ("LeftMiddleIntermediate", "CC_Base_L_Mid2"),
            ("LeftMiddleDistal", "CC_Base_L_Mid3"),
            ("LeftRingProximal", "CC_Base_L_Ring1"),
            ("LeftRingIntermediate", "CC_Base_L_Ring2"),
            ("LeftRingDistal", "CC_Base_L_Ring3"),
            ("LeftLittleProximal", "CC_Base_L_Pinky1"),
            ("LeftLittleIntermediate", "CC_Base_L_Pinky2"),
            ("LeftLittleDistal", "CC_Base_L_Pinky3"),

            ("RightThumbProximal", "CC_Base_R_Thumb1"),
            ("RightThumbIntermediate", "CC_Base_R_Thumb2"),
            ("RightThumbDistal", "CC_Base_R_Thumb3"),
            ("RightIndexProximal", "CC_Base_R_Index1"),
            ("RightIndexIntermediate", "CC_Base_R_Index2"),
            ("RightIndexDistal", "CC_Base_R_Index3"),
            ("RightMiddleProximal", "CC_Base_R_Mid1"),
            ("RightMiddleIntermediate", "CC_Base_R_Mid2"),
            ("RightMiddleDistal", "CC_Base_R_Mid3"),
            ("RightRingProximal", "CC_Base_R_Ring1"),
            ("RightRingIntermediate", "CC_Base_R_Ring2"),
            ("RightRingDistal", "CC_Base_R_Ring3"),
            ("RightLittleProximal", "CC_Base_R_Pinky1"),
            ("RightLittleIntermediate", "CC_Base_R_Pinky2"),
            ("RightLittleDistal", "CC_Base_R_Pinky3"),
        };

        var bones = new HumanBone[map.Length];
        for (int i = 0; i < map.Length; i++)
        {
            bones[i] = new HumanBone
            {
                humanName = map[i].human,
                boneName = map[i].bone,
                limit = new HumanLimit { useDefaultValues = true },
            };
        }

        return bones;
    }

    private static AnimatorController BuildWalkIdleController(string characterName, AnimationClip walkClip)
    {
        string path = $"{ControllerOutputFolder}/{characterName}Locomotion.controller";
        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(path);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        AnimatorState idleState = stateMachine.AddState("Idle");
        AnimatorState walkState = stateMachine.AddState("Walk");
        walkState.motion = walkClip;
        stateMachine.defaultState = idleState;

        AnimatorStateTransition toWalk = idleState.AddTransition(walkState);
        toWalk.hasExitTime = false;
        toWalk.duration = 0.15f;
        toWalk.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");

        AnimatorStateTransition toIdle = walkState.AddTransition(idleState);
        toIdle.hasExitTime = false;
        toIdle.duration = 0.15f;
        toIdle.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");

        return controller;
    }

    private static void WireIntoCharacterPrefab(string prefabPath, AnimatorController controller, Avatar avatar)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
        {
            Debug.LogWarning($"[CharacterAnimationSetup] Character prefab not found at {prefabPath} - skipped wiring.");
            return;
        }

        GameObject instance = PrefabUtility.LoadPrefabContents(prefabPath);

        var animator = instance.GetComponent<Animator>();
        if (animator == null)
        {
            animator = instance.AddComponent<Animator>();
        }
        animator.avatar = avatar;
        animator.runtimeAnimatorController = controller;
        // Movement is driven entirely by PlayerMovement's CharacterController - root motion baked
        // into the clip would otherwise additionally (and redundantly) displace the character.
        animator.applyRootMotion = false;

        if (instance.GetComponent<CharacterAnimationDriver>() == null)
        {
            instance.AddComponent<CharacterAnimationDriver>();
        }

        PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
        PrefabUtility.UnloadPrefabContents(instance);
    }
}
