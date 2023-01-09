using System.Threading.Channels;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Playouts;

public class ReplacePlayoutAlternateScheduleItemsHandler :
    IRequestHandler<ReplacePlayoutAlternateScheduleItems, Either<BaseError, Unit>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ChannelWriter<IBackgroundServiceRequest> _channel;
    private readonly ILogger<ReplacePlayoutAlternateScheduleItemsHandler> _logger;

    public ReplacePlayoutAlternateScheduleItemsHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        ChannelWriter<IBackgroundServiceRequest> channel,
        ILogger<ReplacePlayoutAlternateScheduleItemsHandler> logger)
    {
        _dbContextFactory = dbContextFactory;
        _channel = channel;
        _logger = logger;
    }

    public async Task<Either<BaseError, Unit>> Handle(
        ReplacePlayoutAlternateScheduleItems request,
        CancellationToken cancellationToken)
    {
        // TODO: validate that items is not empty

        try
        {
            await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            Option<Playout> maybePlayout = await dbContext.Playouts
                .Include(p => p.ProgramSchedule)
                .Include(p => p.ProgramScheduleAlternates)
                .ThenInclude(p => p.ProgramSchedule)
                .SelectOneAsync(p => p.Id, p => p.Id == request.PlayoutId);

            foreach (Playout playout in maybePlayout)
            {
                ProgramSchedule existingDefault = playout.ProgramSchedule;
                
                // exclude highest index
                int maxIndex = request.Items.Map(x => x.Index).Max();
                ReplacePlayoutAlternateSchedule highest = request.Items.First(x => x.Index == maxIndex);

                ProgramScheduleAlternate[] existing = playout.ProgramScheduleAlternates.ToArray();

                var incoming = request.Items.Except(new[] { highest }).ToList();

                var toAdd = incoming.Filter(x => existing.All(e => e.Id != x.Id)).ToList();
                var toRemove = existing.Filter(e => incoming.All(m => m.Id != e.Id)).ToList();
                var toUpdate = incoming.Except(toAdd).ToList();

                playout.ProgramScheduleAlternates.RemoveAll(toRemove.Contains);

                foreach (ReplacePlayoutAlternateSchedule add in toAdd)
                {
                    playout.ProgramScheduleAlternates.Add(
                        new ProgramScheduleAlternate
                        {
                            PlayoutId = playout.Id,
                            Index = add.Index,
                            ProgramScheduleId = add.ProgramScheduleId,
                            DaysOfWeek = add.DaysOfWeek,
                            DaysOfMonth = add.DaysOfMonth,
                            MonthsOfYear = add.MonthsOfYear
                        });
                }

                foreach (ReplacePlayoutAlternateSchedule update in toUpdate)
                {
                    foreach (ProgramScheduleAlternate ex in existing.Filter(x => x.Id == update.Id))
                    {
                        ex.Index = update.Index;
                        ex.ProgramScheduleId = update.ProgramScheduleId;
                        ex.DaysOfWeek = update.DaysOfWeek;
                        ex.DaysOfMonth = update.DaysOfMonth;
                        ex.MonthsOfYear = update.MonthsOfYear;
                    }
                }

                // save highest index directly to playout
                if (playout.ProgramScheduleId != highest.ProgramScheduleId)
                {
                    playout.ProgramScheduleId = highest.ProgramScheduleId;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                
                Option<PlayoutItem> maybePlayoutItem = await dbContext.PlayoutItems
                    .Filter(pi => pi.PlayoutId == request.PlayoutId)
                    .OrderByDescending(pi => pi.Start)
                    .FirstOrDefaultAsync(cancellationToken)
                    .Map(Optional);

                foreach (PlayoutItem playoutItem in maybePlayoutItem)
                {
                    DateTimeOffset start = DateTimeOffset.Now;
                    var daysToCheck = Enumerable.Range(0, (playoutItem.StartOffset - start).Days + 1)
                        .Select(d => start.AddDays(d))
                        .ToList();

                    foreach (DateTimeOffset dayToCheck in daysToCheck)
                    {
                        ProgramSchedule oldSchedule = PlayoutScheduleSelector.GetProgramScheduleFor(
                            existingDefault,
                            existing,
                            dayToCheck);

                        ProgramSchedule newSchedule = PlayoutScheduleSelector.GetProgramScheduleFor(
                            playout.ProgramSchedule,
                            playout.ProgramScheduleAlternates,
                            dayToCheck);

                        if (oldSchedule.Id != newSchedule.Id)
                        {
                            _logger.LogInformation(
                                "Alternate schedule change detected for day {Day}, schedule {One} => {Two}; will refresh playout",
                                dayToCheck,
                                oldSchedule.Name,
                                newSchedule.Name);

                            await _channel.WriteAsync(
                                new BuildPlayout(request.PlayoutId, PlayoutBuildMode.Refresh),
                                cancellationToken);

                            break;
                        }
                    }
                }
            }
            
            return Unit.Default;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.Message);
        }
    }
}