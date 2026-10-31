import { Module } from '@nestjs/common';
import { PrismaModule } from '../prisma/prisma.module';
import { SeedService } from './seed.service';

@Module({
  imports: [PrismaModule],
  providers: [SeedService],
  exports: [SeedService],
})
export class SeedModule {}
