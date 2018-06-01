﻿using System;
using System.Threading;
using System.Threading.Tasks;
using DC.JobContextManager.Interface;
using ESFA.DC.Auditing.Interface;
using ESFA.DC.JobContext;
using ESFA.DC.JobContext.Interface;
using ESFA.DC.Logging.Interfaces;
using ESFA.DC.Mapping.Interface;
using ESFA.DC.Queueing.Interface;

namespace DC.JobContextManager
{
    public sealed class JobContextManager<T> : IJobContextManager
        where T : new()
    {
        private readonly IQueuePublishService<JobContextDto> _queuePublishService;

        private readonly IAuditor _auditor;

        private readonly IMapper<JobContextMessage, T> _mapper;

        private readonly Func<T, CancellationToken, Task<bool>> _callback;

        private readonly JobContextMapper _jobContextMapper;

        private readonly ILogger _logger;

        public JobContextManager(IQueueSubscriptionService<JobContextDto> queueSubscriptionService, IQueuePublishService<JobContextDto> queuePublishService, IAuditor auditor, IMapper<JobContextMessage, T> mapper, Func<T, CancellationToken, Task<bool>> callback, ILogger logger)
        {
            _queuePublishService = queuePublishService;
            _auditor = auditor;
            _mapper = mapper;
            _callback = callback;
            _logger = logger;
            _jobContextMapper = new JobContextMapper();
            queueSubscriptionService.Subscribe(Callback);
        }

        public async Task FinishSuccessfully(IJobContextMessage jobContextMessage)
        {
            await _auditor.AuditEndAsync(jobContextMessage);
        }

        public async Task FinishError(IJobContextMessage jobContextMessage)
        {
            await _auditor.AuditJobFailAsync(jobContextMessage);
        }

        private async Task<bool> Callback(JobContextDto jobContextDto, CancellationToken cancellationToken)
        {
            JobContextMessage jobContextMessage = _jobContextMapper.MapTo(jobContextDto);

            try
            {
                await _auditor.AuditStartAsync(jobContextMessage);
                T obj = _mapper.MapTo(jobContextMessage);
                if (!await _callback.Invoke(obj, cancellationToken))
                {
                    await _auditor.AuditJobFailAsync(jobContextMessage);
                    return false;
                }

                jobContextMessage = _mapper.MapFrom(obj);
                jobContextMessage.TopicPointer++;
                await _auditor.AuditEndAsync(jobContextMessage);
                if (jobContextMessage.TopicPointer >= jobContextMessage.Topics.Count)
                {
                    return true;
                }

                jobContextDto = _jobContextMapper.MapFrom(jobContextMessage);
                await _queuePublishService.PublishAsync(jobContextDto);
            }
            catch (Exception ex)
            {
                _logger.LogError("Exception thrown in JobManager callback", ex);
                await _auditor.AuditJobFailAsync(jobContextMessage);
                return false;
            }

            return true;
        }
    }
}
