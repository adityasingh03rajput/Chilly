import { Controller, Get, Post } from '@nestjs/common';
import { AppService } from './app.service';
import { SeedService } from './seed/seed.service';

@Controller()
export class AppController {
  constructor(
    private readonly appService: AppService,
    private readonly seedService: SeedService,
  ) {}

  @Get()
  getHello(): string {
    return this.appService.getHello();
  }

  @Get('health')
  getHealth() {
    return { status: 'ok' };
  }

  @Get('seed')
  @Post('seed')
  async triggerSeed() {
    return this.seedService.runSeed();
  }
}
