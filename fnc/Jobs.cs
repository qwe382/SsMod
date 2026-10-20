using Content.Shared.Access.Components;
using Content.Shared.Roles;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace SsMod.Fnc;

// Профессия персонажа — по его ID-карте. Общее для ESP (строка над именем)
// и фрейма «Персонажи» (колонка таблицы). Раньше это жило внутри ESP.
internal static class Jobs
{
    // Профессия ищется по контейнерам цели, а это дорого, поэтому кэшируем её
    // на секунду. Вместе с ней держим причину, по которой её не нашли: она
    // показывается вместо профессии, чтобы было видно, где обрывается поиск.
    private static readonly Dictionary<EntityUid, (string? Job, string Reason, TimeSpan At)> Cache = new();

    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(1);

    // now — _timing.CurTime. Замер из будущего (часы поехали) считаем протухшим.
    public static string? Of(IEntityManager ent, EntityUid uid, TimeSpan now, out string reason)
    {
        if (Cache.TryGetValue(uid, out var cached)
            && now >= cached.At && now - cached.At < CacheTime)
        {
            reason = cached.Reason;
            return cached.Job;
        }

        var job = Read(ent, uid, out reason);

        if (Cache.Count > 512)
            Cache.Clear();

        Cache[uid] = (job, reason, now);
        return job;
    }

    // Профессия берётся с ID-карты цели: карточка лежит в слоте id (или внутри
    // КПК), а содержимое контейнеров на клиенте есть. Сначала пробуем прототип
    // профессии (у него уже локализованное имя), иначе локализуем JobTitle.
    private static string? Read(IEntityManager ent, EntityUid uid, out string reason)
    {
        reason = "нет карты";

        if (!TryFindCard(ent, uid, out var card))
            return null;

        if (!ent.TryGetComponent(card, out IdCardComponent? idCard))
        {
            reason = "карта без компонента";
            return null;
        }

        if (idCard.JobPrototype is { } jobId)
        {
            var protos = IoCManager.Resolve<IPrototypeManager>();

            if (protos.TryIndex(jobId, out JobPrototype? job) && !string.IsNullOrWhiteSpace(job.LocalizedName))
                return job.LocalizedName;

            reason = $"прототип {jobId.Id} пуст";
        }
        else if (idCard.JobTitle is { } title)
        {
            var loc = IoCManager.Resolve<ILocalizationManager>();
            return loc.TryGetString(title.Id, out var text) ? text : title.Id;
        }
        else
        {
            var proto = ent.TryGetComponent(card, out MetaDataComponent? meta)
                ? meta.EntityPrototype?.ID ?? meta.EntityName
                : "?";

            // В этих сборках профессия карточки лежит не в компоненте, а в самом
            // прототипе карты (MedicalInternIDCard — это PresetIdCard с
            // job: MedicalIntern), а PresetIdCard есть только на сервере, и в
            // IdCardComponent клиента JobPrototype/JobTitle не приезжают.
            // Поэтому профессию ищем по прототипу: у профессии id такой, что
            // «<профессия>IDCard» — это прототип карты.
            if (proto != "?" && JobByCardProto(proto) is { } derived)
                return derived;

            reason = $"карта ({proto}) без профессии";
        }

        return null;
    }

    // Прототипы карт, разложенные по профессиям, разбираем один раз за сессию.
    private static readonly Dictionary<string, string?> CardProtoJobs = new();

    private static string? JobByCardProto(string cardProto)
    {
        if (CardProtoJobs.TryGetValue(cardProto, out var cached))
            return cached;

        var protos = IoCManager.Resolve<IPrototypeManager>();
        string? found = null;

        foreach (var job in protos.EnumeratePrototypes<JobPrototype>())
        {
            if (!string.Equals(cardProto, job.ID + "IDCard", StringComparison.OrdinalIgnoreCase))
                continue;

            found = job.LocalizedName;
            break;
        }

        CardProtoJobs[cardProto] = found;
        return found;
    }

    // Карточка обычно в слоте id/КПК, а у КПК — во внутреннем контейнере,
    // поэтому обходим контейнеры в ширину на пару уровней вглубь.
    private static bool TryFindCard(IEntityManager ent, EntityUid root, out EntityUid card)
    {
        card = default;
        var level = new List<EntityUid> { root };

        for (var depth = 0; depth < 3 && level.Count > 0; depth++)
        {
            var next = new List<EntityUid>();

            foreach (var current in level)
            {
                if (!ent.TryGetComponent(current, out ContainerManagerComponent? containers))
                    continue;

                foreach (var (_, container) in containers.Containers)
                {
                    foreach (var child in container.ContainedEntities)
                    {
                        if (ent.TryGetComponent(child, out IdCardComponent? _))
                        {
                            card = child;
                            return true;
                        }

                        next.Add(child);
                    }
                }
            }

            level = next;
        }

        return false;
    }
}
