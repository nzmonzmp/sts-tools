using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Random;

namespace StsTools.Game;

/// <summary>
/// Monstres d'une rencontre dessinés avec leurs visuels de combat : survol d'une rencontre dans l'écran Stats (onglet
/// Losses, animé) et petites images fixes de l'aperçu des salles sur la carte (<see cref="Thumbnail"/>). Le rendu se
/// fait dans des scènes à fond transparent (SubViewport), dont on ne garde que les pixels dessinés.
/// </summary>
public static class EncounterVisuals
{
    /// <summary>
    /// Une composition possible de la rencontre (monstres et emplacements), tirée avec un hasard à part : rien ne
    /// change dans la partie. Si le tirage échoue, tous les monstres possibles.
    /// </summary>
    public static (EncounterModel Encounter, List<(MonsterModel Monster, string? Slot)> Monsters)? Generate(EncounterModel canonical)
    {
        try
        {
            var encounter = canonical.ToMutable();
            AccessTools.Field(typeof(EncounterModel), "_rng").SetValue(encounter, new Rng((ulong)System.Random.Shared.NextInt64()));
            var generated = (IReadOnlyList<(MonsterModel, string?)>)AccessTools.Method(typeof(EncounterModel), "GenerateMonsters")
                .Invoke(encounter, null)!;
            return (encounter, generated.Select(m => (m.Item1, m.Item2)).ToList());
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot generate {canonical.Id.Entry}, showing all possible monsters: {e.Message}");
            try
            {
                return (canonical, canonical.AllPossibleMonsters.Distinct().Select(m => (m, (string?)null)).ToList());
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Scènes à rendre pour une rencontre. Rencontre avec une scène d'emplacements (Decimillipede, Kaiser Crab...) : la
    /// scène entière, chaque monstre posé sur son emplacement comme en combat ; sinon un monstre par scène (5 au plus).
    /// </summary>
    public static List<SubViewport> Render(Func<Vector2I, SubViewport> newViewport, string encounterId, EncounterModel encounter,
        List<(MonsterModel Monster, string? Slot)> monsters)
    {
        var viewports = new List<SubViewport>();
        if (WholeCreatureScenes.TryGetValue(encounterId, out var scenePath))
            viewports.Add(RenderScene(newViewport, scenePath));
        else if (encounter.HasScene && monsters.All(m => m.Slot != null))
            viewports.Add(RenderEncounterScene(newViewport, encounter, monsters));
        else
            foreach (var (monster, _) in monsters.Take(5))
                if (RenderMonster(newViewport, monster) is { } viewport)
                    viewports.Add(viewport);
        return viewports;
    }

    /// <summary>
    /// Rencontres dont les monstres n'ont pas de dessin propre : la créature entière est une scène à part,
    /// placée par le jeu dans le décor du combat (et dans le Bestiaire). Kaiser Crab : Crusher et Rocket ne sont
    /// que les zones de ses pinces.
    /// </summary>
    private static readonly Dictionary<string, string> WholeCreatureScenes = new()
    {
        ["KAISER_CRAB_BOSS"] = "res://scenes/creature_visuals/kaiser_crab_boss_setup.tscn",
    };

    /// <summary>Visuels de combat d'un monstre, avec son apparence et son animation d'attente.</summary>
    private static NCreatureVisuals AddVisuals(Node parent, MonsterModel monster)
    {
        var visuals = monster.CreateVisuals();
        parent.AddChild(visuals);
        try
        {
            // Apparence choisie à la création en combat (Scroll of Biting...) : sans elle, rien n'est dessiné.
            // Certains monstres la calculent d'après leur créature de combat, absente ici (Phantasmal Gardeners).
            visuals.SetUpSkin(monster);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] default skin for {monster.Id}: {e.Message}");
        }
        // Animation de départ du monstre, comme le jeu en combat (NCreature) : Cubex Construct commence enterré ; à
        // défaut, l'animation d'attente
        bool started = false;
        if (visuals.SpineBody != null)
            try
            {
                monster.GenerateAnimator(visuals.SpineBody);
                started = true;
            }
            catch (Exception)
            {
                // Animateur qui a besoin de la créature de combat : animation d'attente
            }
        if (!started && visuals.SpineBody?.GetSkeleton()?.GetData().HasAnimation("idle_loop") == true)
            visuals.SpineBody.GetAnimationState().SetAnimation("idle_loop");
        StopDeathParticles(visuals);
        return visuals;
    }

    /// <summary>
    /// Coupe les particules de mort (Infested Prism...) : Godot les émet dès la création, et leur nuage
    /// agrandissait la zone dessinée.
    /// </summary>
    private static void StopDeathParticles(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child.Name.ToString().Contains("death", StringComparison.OrdinalIgnoreCase))
            {
                if (child is GpuParticles2D gpu)
                {
                    gpu.Emitting = false;
                    gpu.Visible = false;
                }
                else if (child is CpuParticles2D cpu)
                {
                    cpu.Emitting = false;
                    cpu.Visible = false;
                }
            }
            StopDeathParticles(child);
        }
    }

    /// <summary>Une scène de créature entière (voir WholeCreatureScenes), avec une marge autour.</summary>
    private static SubViewport RenderScene(Func<Vector2I, SubViewport> newViewport, string path)
    {
        // Grandes marges : la carapace et les pinces de Kaiser Crab dépassent largement le cadre de sa scène
        var viewport = newViewport(new Vector2I(1920 + 2400, 1080 + 1640));
        var scene = ResourceLoader.Load<PackedScene>(path).Instantiate();
        viewport.AddChild(scene);
        // Le décor de Kaiser Crab se masque lui-même au démarrage ; le Bestiaire le réaffiche
        if (scene is CanvasItem item)
            item.Visible = true;
        if (scene is Node2D node2D)
            node2D.Position = new Vector2(1200, 1280);
        else if (scene is Control control)
            control.Position = new Vector2(1200, 1280);
        StopDeathParticles(scene);
        return viewport;
    }

    /// <summary>Un monstre seul, centré sur son cadre de combat dans une scène assez grande.</summary>
    private static SubViewport? RenderMonster(Func<Vector2I, SubViewport> newViewport, MonsterModel monster)
    {
        try
        {
            var viewport = newViewport(new Vector2I(2048, 2048));
            var visuals = AddVisuals(viewport, monster);
            var bounds = visuals.Bounds.GetRect();
            var size = new Vector2I(
                (int)Math.Clamp(bounds.Size.X * 3, 512, 2048), (int)Math.Clamp(bounds.Size.Y * 3, 512, 2048));
            viewport.Size = size;
            visuals.Position = (Vector2)size / 2 - (bounds.Position + bounds.Size / 2);
            return viewport;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot show monster {monster.Id}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// La scène d'emplacements de la rencontre (taille d'écran de combat, 1920 × 1080), avec une marge autour,
    /// et chaque monstre posé sur son emplacement comme dans NCombatRoom.
    /// </summary>
    private static SubViewport RenderEncounterScene(Func<Vector2I, SubViewport> newViewport, EncounterModel encounter,
        List<(MonsterModel Monster, string? Slot)> monsters)
    {
        var margin = new Vector2(640, 360);
        var viewport = newViewport(new Vector2I(1920 + 1280, 1080 + 720));
        var scene = encounter.CreateScene();
        viewport.AddChild(scene);
        scene.Position = margin;
        foreach (var (monster, slot) in monsters)
        {
            var marker = scene.GetNodeOrNull<Marker2D>(slot!);
            if (marker == null)
                continue;
            try
            {
                var visuals = AddVisuals(viewport, monster);
                visuals.Position = marker.GlobalPosition;
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] cannot show monster {monster.Id}: {e.Message}");
            }
        }
        return viewport;
    }

    /// <summary>
    /// Rectangle des pixels assez opaques de l'image. Image.GetUsedRect compte aussi les halos presque
    /// transparents (Infested Prism), qui agrandissaient le panneau pour rien.
    /// </summary>
    public static Rect2I OpaqueRect(Image image, byte minAlpha = 40)
    {
        if (image.GetFormat() != Image.Format.Rgba8)
            image.Convert(Image.Format.Rgba8);
        int width = image.GetWidth(), height = image.GetHeight();
        var data = image.GetData();
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                if (data[row + x * 4 + 3] < minAlpha)
                    continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                maxY = y;
            }
        }
        return maxX < 0 ? new Rect2I() : new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    // ---- Petites images fixes (aperçu des salles) ----

    /// <summary>Hauteur des images gardées en mémoire (affichées plus petites).</summary>
    private const int ThumbnailHeight = 96;

    /// <summary>Hauteur maximale d'un monstre avant la mise côte à côte, comme dans l'écran Stats.</summary>
    private const float MonsterHeight = 260;

    private static readonly Dictionary<string, Texture2D?> Thumbnails = new();
    private static readonly Dictionary<string, List<Action<Texture2D>>> Waiting = new();
    private static readonly Queue<EncounterModel> Pending = new();
    private static bool _busy;

    /// <summary>
    /// Image fixe des monstres d'une rencontre, déjà faite ; sinon null, et <paramref name="ready"/> est appelé quand elle
    /// l'est (une rencontre à la fois, quelques images plus tard). Gardée pour toute la session.
    /// </summary>
    public static Texture2D? Thumbnail(EncounterModel encounter, Action<Texture2D>? ready = null)
    {
        string id = encounter.Id.Entry;
        if (Thumbnails.TryGetValue(id, out var done))
            return done;
        if (!Waiting.TryGetValue(id, out var callbacks))
        {
            Waiting[id] = callbacks = [];
            Pending.Enqueue(encounter);
        }
        if (ready != null)
            callbacks.Add(ready);
        Next();
        return null;
    }

    private static void Next()
    {
        if (_busy || Pending.Count == 0 || Engine.GetMainLoop() is not SceneTree tree)
            return;
        _busy = true;
        var encounter = Pending.Dequeue();
        string id = encounter.Id.Entry;
        var holder = new Node { Name = "StsToolsEncounterThumbnail" };
        tree.Root.AddChild(holder);
        var viewports = new List<SubViewport>();
        try
        {
            if (Generate(encounter) is { } preview)
                viewports = Render(size =>
                {
                    var viewport = new SubViewport
                    {
                        TransparentBg = true, Disable3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Size = size,
                    };
                    holder.AddChild(viewport);
                    return viewport;
                }, id, preview.Encounter, preview.Monsters);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] cannot render encounter {id}: {e.Message}");
        }
        // Le temps que Spine pose les monstres et finisse les animations de départ (Cubex Construct s'enterre)
        tree.CreateTimer(0.8).Timeout += () =>
        {
            Texture2D? texture = null;
            try
            {
                texture = Compose(viewports);
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] cannot render encounter {id}: {e.Message}");
            }
            if (GodotObject.IsInstanceValid(holder))
                holder.QueueFree();
            Thumbnails[id] = texture;
            if (Waiting.Remove(id, out var callbacks) && texture != null)
                foreach (var callback in callbacks)
                    try
                    {
                        callback(texture);
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"[{ModEntry.Id}] encounter thumbnail: {e.Message}");
                    }
            _busy = false;
            Next();
        };
    }

    /// <summary>
    /// Pixels dessinés de chaque scène, monstres mis à l'échelle comme dans l'écran Stats, posés côte à côte par les pieds,
    /// puis réduits à <see cref="ThumbnailHeight"/>.
    /// </summary>
    private static Texture2D? Compose(List<SubViewport> viewports)
    {
        const int gap = 16;
        var parts = new List<Image>();
        foreach (var viewport in viewports)
        {
            if (!GodotObject.IsInstanceValid(viewport))
                continue;
            var image = viewport.GetTexture().GetImage();
            var used = OpaqueRect(image);
            if (used.Size.X <= 0 || used.Size.Y <= 0)
                continue;
            var region = new Rect2I(used.Position - new Vector2I(4, 4), used.Size + new Vector2I(8, 8))
                .Intersection(new Rect2I(Vector2I.Zero, image.GetSize()));
            var part = image.GetRegion(region);
            float scale = Math.Min(1f, MonsterHeight / part.GetHeight());
            part.Resize(Math.Max(1, (int)(part.GetWidth() * scale)), Math.Max(1, (int)(part.GetHeight() * scale)),
                Image.Interpolation.Bilinear);
            parts.Add(part);
        }
        if (parts.Count == 0)
            return null;
        int width = parts.Sum(p => p.GetWidth()) + gap * (parts.Count - 1);
        int height = parts.Max(p => p.GetHeight());
        var canvas = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        int x = 0;
        foreach (var part in parts)
        {
            canvas.BlendRect(part, new Rect2I(Vector2I.Zero, part.GetSize()), new Vector2I(x, height - part.GetHeight()));
            x += part.GetWidth() + gap;
        }
        float fit = (float)ThumbnailHeight / height;
        if (fit < 1)
            canvas.Resize(Math.Max(1, (int)(width * fit)), ThumbnailHeight, Image.Interpolation.Lanczos);
        return ImageTexture.CreateFromImage(canvas);
    }
}
