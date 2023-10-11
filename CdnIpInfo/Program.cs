using AlibabaCloud.OpenApiClient.Models;
using AlibabaCloud.SDK.Cdn20180510;
using AlibabaCloud.SDK.Cdn20180510.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace YourNamespace
{
    class Program
    {

        static async Task Main(string[] args)
        {
            // 读取IP列表文件
            Console.WriteLine("请输入需要批量查询的TXT文件路径，并按回车键继续...");
            string ipListFilePath = Console.ReadLine();
            // 获取输入路径的目录部分  
            string directoryPath = Path.GetDirectoryName(ipListFilePath);
            //读取文件中的IP列表，去除空行
            string[] ipList = File.ReadAllLines(ipListFilePath).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            // 去重IP列表
            string[] distinctIpList = ipList.Distinct().ToArray();
            File.WriteAllLines(ipListFilePath, distinctIpList);
            Console.WriteLine($"检测到当前目录下iplist.txt文件去重后共有 {distinctIpList.Length} 个 IP");
            // 输入阿里云的AccessKey
            Console.Write("请输入AccesskeyId并按回车键继续：");
            string accessKeyId = Console.ReadLine();
            Console.Write("请输入AccessKeySecret并按回车键继续：");
            string accessKeySecret = Console.ReadLine();
            // 输入接口限制的QPS
            Console.Write("请输入接口限制的 QPS：");
            int qps = int.Parse(Console.ReadLine());
            int totalRequests = distinctIpList.Length;
            // 计算推荐的线程数
            int recommendedThreadCount = Math.Min(Environment.ProcessorCount * 2, totalRequests / qps + 1);
            Console.WriteLine($"推荐设置的线程数：{recommendedThreadCount}");
            // 创建 CDN 的 Client 实例
            // var client = CreateClient(accessKeyId, accessKeySecret);
            // 输入用户指定的线程数
            Console.Write("请输入线程数：");
            int threadCount = int.Parse(Console.ReadLine());
            // 用户输入线程数和推荐线程数取最小值，保护机制，不超过cpu核心数*2
            threadCount = Math.Min(threadCount, recommendedThreadCount);
            // 定义存储查询结果的并发集合
            ConcurrentBag<string> trueIPs = new ConcurrentBag<string>();
            ConcurrentBag<string> falseIPs = new ConcurrentBag<string>();
            // 创建 Stopwatch 实例  
            Stopwatch stopwatch = new Stopwatch();
            //下面最后将response结果写入"result.txt"文件时需要用到，确保一段代码在同一时刻只能由一个线程访问，避免数据竞争导致不一致的状态
            object locker = new object();
            //已完成的请求数
            int completedRequests = 0;
            // 创建连接池，用于复用CdnClient对象
            // 因为阿里云SDK会自动处理资源的释放。因此，不需要在代码中显式调用 client.Dispose()，每次都会自动释放，那么连接池就没办法复用
            //var clientPool = new ConcurrentBag<Client>();
            // 将开始时间置为零然后开始计时
            stopwatch.Reset();
            stopwatch.Start();
            // for (int i = 0; i < threadCount; i++)
            // {
            //     var client = CreateClient(accessKeyId, accessKeySecret);
            //     clientPool.Add(client);
            // }
            // 创建任务列表
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < threadCount; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    while (true)
                    {
                        // 使用原子操作获取当前已完成的请求数，后面-1意思是index从0开始计
                        int index = System.Threading.Interlocked.Increment(ref completedRequests) - 1;
                        if (index >= totalRequests)
                        {
                            return;
                        }
                        // 获取当前需要查询的IP地址
                        string ipAddress = distinctIpList[index];
                        // 验证IP地址是否合法
                        IPAddress ip;
                        if (!IPAddress.TryParse(ipAddress, out ip))
                        {
                            Console.WriteLine($"无效的IP地址：{ipAddress}");
                            continue;
                        }
                        try
                        {
                            Client client;
                            //TryTake方法返回的是bool，取到client返回true，没取到就是false,!代表取反的意思
                            //if (!clientPool.TryTake(out client))
                            //{
                            //    // 连接池为空时，创建新的CdnClient对象
                            client = CreateClient(accessKeyId, accessKeySecret);
                            //}
                            // 创建查询请求
                            var request = new DescribeIpInfoRequest
                            {
                                IP = ipAddress
                            };
                            // 发送查询请求的异步操作
                            var queryTask = client.DescribeIpInfoAsync(request);
                            // 设置超时等待的时间（毫秒）
                            int timeout = 60000;
                            // 等待任意一个任务完成（发送查询请求、或超时），await返回值是Task<Task> 对象，表示哪个任务已经完成。
                            var completedTask = await Task.WhenAny(queryTask, Task.Delay(timeout));
                            if (completedTask == queryTask)
                            {
                                // 查询请求已完成，获取响应，await表示等待queryTask任务完成，而不是直接使用queryTask
                                var response = await queryTask;
                                // 根据查询结果将IP地址添加到对应的集合中
                                if (response.Body.CdnIp == "True")
                                {
                                    //Console.WriteLine("当前查询ip：" + ipAddress + "是阿里云CDN节点ip，" + "本次请求Requestid：" + response.Body.RequestId);
                                    trueIPs.Add(ipAddress);
                                }
                                else
                                {
                                    //Console.WriteLine("当前查询ip：" + ipAddress + "非阿里云CDN节点ip，" + "本次请求Requestid：" + response.Body.RequestId);
                                    falseIPs.Add(ipAddress);
                                }

                                // 将响应的Body转换为JSON格式的字符串并写入到"result.txt"文件      
                                string result = JsonConvert.SerializeObject(response.Body);
                                lock (locker)
                                {
                                    string filePath = Path.Combine(directoryPath, "result.txt");
                                    if (!File.Exists(filePath))
                                    {
                                        File.WriteAllText(filePath, result);
                                    }
                                    else
                                    {
                                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                                        result = Environment.NewLine + timestamp + Environment.NewLine + result;
                                        File.AppendAllText(filePath, result);
                                    }
                                }
                                // //判断连接是否超过3000ms未使用，如果超过则释放
                                // //这块不需要了，因为阿里云CDNSDK会自动释放client，不需要手动操作
                                // if (client.LastUsedTime != DateTime.MinValue && (DateTime.Now - client.LastUsedTime).TotalMilliseconds > 3000)
                                // {
                                //    client.Dispose();
                                // }
                                // else
                                // {
                                //    // 将CdnClient对象放回连接池中供复用
                                //    clientPool.Add(client);
                                // }
                                // clientPool.Add(client);                                
                            }

                            else
                            {
                                // 超时时间到达，取消查询请求
                                //client.Dispose();
                                Console.WriteLine("当前查询ip：" + ipAddress + "查询超时，超时时间：" + timeout + "ms");
                            }
                            // 显示进度条
                            int currentRequests = System.Threading.Interlocked.Add(ref completedRequests, 0);
                            DrawProgressBar(currentRequests, totalRequests);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"查询 IP 信息出错：{ex.Message}");
                        }
                    }
                }));
            }
            // 等待所有任务完成
            await Task.WhenAll(tasks);
            // 停止计时  
            stopwatch.Stop();
            // 将结果写入文件
            string tureIPsFilePath = Path.Combine(Path.GetDirectoryName(ipListFilePath), "True.txt");
            string falseIPsFilePath = Path.Combine(Path.GetDirectoryName(ipListFilePath), "False.txt");
            File.WriteAllLines(tureIPsFilePath, trueIPs);
            File.WriteAllLines(falseIPsFilePath, falseIPs);
            Console.WriteLine($"True.txt中共包含 {trueIPs.Count} 个IP地址");
            Console.WriteLine($"False.txt中共包含 {falseIPs.Count} 个IP地址");
            // 打印运行时间  
            Console.WriteLine("程序运行查询总耗时时间：{0} 毫秒", stopwatch.ElapsedMilliseconds);
            // 释放连接池中的客户端对象
            //这块不需要了，因为阿里云CDNSDK会自动释放client，不需要手动操作
            //foreach (var client in clientPool)
            //{
            //    client.Dispose();
            //}
            Console.WriteLine("程序运行完毕，按下任意键退出...");
            Console.ReadKey();
        }
        public static Client CreateClient(string accessKeyId, string accessKeySecret)
        {
            Config config = new Config
            {
                // 必填，您的 AccessKey ID
                AccessKeyId = accessKeyId,
                // 必填，您的 AccessKey Secret
                AccessKeySecret = accessKeySecret,
            };
            config.Endpoint = "cdn.aliyuncs.com";
            return new Client(config);
        }
        static void DrawProgressBar(int completed, int total)
        {
            const int ProgressBarWidth = 50;
            Console.CursorVisible = false;
            int width = Console.WindowWidth - 1;

            float progress = (float)completed / total;
            int chars = (int)Math.Round(progress * ProgressBarWidth);

            // 清除当前行的内容  
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', width));
            Console.SetCursorPosition(0, Console.CursorTop);

            Console.Write("[");
            Console.BackgroundColor = ConsoleColor.Green;
            Console.Write(new string('#', chars));
            Console.BackgroundColor = ConsoleColor.DarkGray;
            Console.Write(new string(' ', ProgressBarWidth - chars));
            Console.ResetColor();
            Console.Write("] ");
            Console.Write($"{completed}/{total} ({progress:P0})");

            if (chars == ProgressBarWidth)
            {
                Console.WriteLine();
            }
        }
    }
}